using TestFramework.Core.Steps;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Core.Artifacts;
using TestFramework.Azure;
using TestFramework.Core.Environment.Graph;
using TestFramework.Core.Environment;
using TestFramework.Core.Logging;
using TestFramework.Core.Variables;

namespace TestFramework.Container.Azure.Components;

internal sealed class AzuriteEnvComponent : DockerAzureEnvComponent
{
    public override EnvComponentIdentifier Id => DockerAzureEnvironment.AzuriteComponentId;

    public override EnvComponentReuseMode ReuseMode => EnvComponentReuseMode.PersistentContext;

    public override IReadOnlyList<EnvComponentIdentifier> Dependencies => [DockerAzureEnvironment.NetworkComponentId];

    public override async Task<object?> CreateAsync(IEnvironmentProvider environment, RunContext context)
    {
        DockerAzureEnvironment dockerEnvironment = GetDockerEnvironment(environment);
        if (dockerEnvironment.UsedStorageIdentifiers.Count == 0)
        {
            context.Logger.LogInformation("Skipping Azurite environment setup because no storage identifiers were requested.");
            return null;
        }

        EnvComponentResourceGuard.EnsureSupplied(context, AzureEnvironmentResourceKinds.StorageKind, dockerEnvironment.UsedStorageIdentifiers, "Azurite environment setup");
        INetwork network = dockerEnvironment.GetRequiredRuntimeState<INetwork>(DockerAzureEnvironment.NetworkComponentId);
        IContainer container = new ContainerBuilder(dockerEnvironment.GetAzuriteImage())
            .WithNetwork(network)
            .WithNetworkAliases(DockerAzureEnvironment.AzuriteNetworkAlias)
            .WithPortBinding(10000, true)
            .WithPortBinding(10001, true)
            .WithPortBinding(10002, true)
            .WithCreateParameterModifier(ContainerPortBinding.Apply)
            .WithCommand("azurite", "--blobHost", "0.0.0.0", "--queueHost", "0.0.0.0", "--tableHost", "0.0.0.0", "--skipApiVersionCheck")
            .Build();

        await container.StartAsync(context.Deadline.Token).ConfigureAwait(false);

        string connectionString = dockerEnvironment.GetEndpointMap().CreateAzuriteConnectionString(container);
        ConnectionStringGuards.EnsureAzurite(connectionString);

        // Published rather than written back into the Azure package's configuration store. That store holds
        // what a person declared, and a mapped port is not something a person can declare - it is chosen when
        // the container starts. Where a resource ended up belongs to the run.
        foreach (string identifier in dockerEnvironment.UsedStorageIdentifiers)
        {
            PublishOn(context).Produce(
                AzureEnvironmentResourceKinds.StorageKind,
                identifier,
                ValueNames.ConnectionString,
                ResourceVantage.Host,
                connectionString);
        }

        dockerEnvironment.SetRuntimeState(Id, container);
        return container;
    }

    public override async Task DeconstructAsync(object? state, IEnvironmentProvider environment, RunContext context)
    {
        if (state is IContainer container)
        {
            await ForceRemoveContainerAsync(container, context.Deadline.Token).ConfigureAwait(false);
        }
        else if (state is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
    }
}