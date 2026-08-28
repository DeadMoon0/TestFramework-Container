using TestFramework.Core.Steps;
using Azure.Messaging.ServiceBus.Administration;
using DotNet.Testcontainers.Networks;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Testcontainers.MsSql;
using Testcontainers.ServiceBus;
using TestFramework.Core.Artifacts;
using TestFramework.Azure;
using TestFramework.Core.Environment.Graph;
using TestFramework.Core.Environment;
using TestFramework.Core.Logging;
using TestFramework.Core.Variables;

namespace TestFramework.Container.Azure.Components;

internal sealed class ServiceBusEnvComponent : DockerAzureEnvComponent
{
    public override EnvComponentIdentifier Id => DockerAzureEnvironment.ServiceBusComponentId;

    public override EnvComponentReuseMode ReuseMode => EnvComponentReuseMode.PersistentContext;

    public override IReadOnlyList<EnvComponentIdentifier> Dependencies => [DockerAzureEnvironment.NetworkComponentId, DockerAzureEnvironment.MsSqlComponentId];

    public override async Task<object?> CreateAsync(IEnvironmentProvider environment, RunContext context)
    {
        DockerAzureEnvironment dockerEnvironment = GetDockerEnvironment(environment);
        if (dockerEnvironment.UsedServiceBusIdentifiers.Count == 0)
        {
            context.Logger.LogInformation("Skipping Service Bus environment setup because no Service Bus identifiers were requested.");
            return null;
        }

        // Called for its effect, not its result: it refuses a used identifier with no configuration entry, and
        // seeds the store the run resolves for the ones that have defaults. Nothing writes to it any more.
        EnvComponentResourceGuard.EnsureSupplied(context, AzureEnvironmentResourceKinds.ServiceBusKind, dockerEnvironment.UsedServiceBusIdentifiers, "Service Bus environment setup");
        INetwork network = dockerEnvironment.GetRequiredRuntimeState<INetwork>(DockerAzureEnvironment.NetworkComponentId);
        MsSqlContainer msSqlContainer = dockerEnvironment.GetRequiredRuntimeState<MsSqlContainer>(DockerAzureEnvironment.MsSqlComponentId);
        MaterializedServiceBusTopology materializedTopology = ServiceBusTopologyMaterializer.Materialize(dockerEnvironment.GetServiceBusTopologySource(), context.Logger);

        string serviceBusImage = dockerEnvironment.GetServiceBusImage();

        // Recorded on the run because nobody stated it: the tag lives in this package's defaults, so a run
        // that passed could not say which image proved it and a bump would change every consumer's result
        // with no diff on their side. §5's third demand, and the reason a finished run is worth handing over.
        context.EffectiveSettings.Record(ImageSource, "servicebus:Image", serviceBusImage);

        ServiceBusContainer container = new ServiceBusBuilder(serviceBusImage)
            .WithAcceptLicenseAgreement(true)
            .WithMsSqlContainer(network, msSqlContainer, ServiceBusBuilder.DatabaseNetworkAlias, dockerEnvironment.GetMsSqlPassword())
            .WithConfig(materializedTopology.ConfigPath)
            .WithNetworkAliases(DockerAzureEnvironment.ServiceBusNetworkAlias)
            .WithCreateParameterModifier(ContainerPortBinding.Apply)
            .Build();

        await container.StartAsync(context.Deadline.Token).ConfigureAwait(false);

        string connectionString = container.GetConnectionString();
        ConnectionStringGuards.EnsureServiceBus(connectionString);

        ServiceBusAdministrationClient administrationClient = new(container.GetHttpConnectionString());
        await administrationClient.GetNamespacePropertiesAsync(context.Deadline.Token).ConfigureAwait(false);

        // Published rather than written back into the Azure package's configuration store. That store holds
        // what a person declared, and a mapped port is not something a person can declare - it is chosen when
        // the container starts. Where a resource ended up belongs to the run.
        foreach (string identifier in dockerEnvironment.UsedServiceBusIdentifiers)
        {
            PublishOn(context).Produce(
                AzureEnvironmentResourceKinds.ServiceBusKind,
                identifier,
                ValueNames.ConnectionString,
                ResourceVantage.Host,
                connectionString);
        }

        ServiceBusRuntimeState runtimeState = new(container, materializedTopology.IsTemporary ? materializedTopology.ConfigPath : null);
        dockerEnvironment.SetRuntimeState(Id, runtimeState);
        return runtimeState;
    }

    public override async Task DeconstructAsync(object? state, IEnvironmentProvider environment, RunContext context)
    {
        if (state is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
    }
}