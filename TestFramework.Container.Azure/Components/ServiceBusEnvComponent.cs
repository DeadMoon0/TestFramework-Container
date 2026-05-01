using Azure.Messaging.ServiceBus.Administration;
using DotNet.Testcontainers.Networks;
using Testcontainers.MsSql;
using Testcontainers.ServiceBus;
using TestFramework.Azure.Configuration;
using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;
using TestFramework.Core.Logging;
using TestFramework.Core.Variables;

namespace TestFramework.Container.Azure.Components;

internal sealed class ServiceBusEnvComponent : DockerAzureEnvComponent
{
    public override EnvComponentIdentifier Id => DockerAzureEnvironment.ServiceBusComponentId;

    public override IReadOnlyList<EnvComponentIdentifier> Dependencies => [DockerAzureEnvironment.NetworkComponentId, DockerAzureEnvironment.MsSqlComponentId];

    public override async Task<object?> CreateAsync(IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        DockerAzureEnvironment dockerEnvironment = GetDockerEnvironment(environment);
        ConfigStore<ServiceBusConfig>? configStore = EnvComponentConfigStoreGuard.GetRequiredStore<ServiceBusConfig>(serviceProvider, dockerEnvironment.UsedServiceBusIdentifiers, "Service Bus environment setup");
        INetwork network = dockerEnvironment.GetRequiredRuntimeState<INetwork>(DockerAzureEnvironment.NetworkComponentId);
        MsSqlContainer msSqlContainer = dockerEnvironment.GetRequiredRuntimeState<MsSqlContainer>(DockerAzureEnvironment.MsSqlComponentId);
        MaterializedServiceBusTopology materializedTopology = ServiceBusTopologyMaterializer.Materialize(dockerEnvironment.GetServiceBusTopologySource());

        ServiceBusContainer container = new ServiceBusBuilder(dockerEnvironment.GetServiceBusImage())
            .WithAcceptLicenseAgreement(true)
            .WithMsSqlContainer(network, msSqlContainer, ServiceBusBuilder.DatabaseNetworkAlias, dockerEnvironment.GetMsSqlPassword())
            .WithConfig(materializedTopology.ConfigPath)
            .WithNetworkAliases(DockerAzureEnvironment.ServiceBusNetworkAlias)
            .Build();

        await container.StartAsync(cancellationToken).ConfigureAwait(false);

        string connectionString = container.GetConnectionString();
        ConnectionStringGuards.EnsureServiceBus(connectionString);

        ServiceBusAdministrationClient administrationClient = new(container.GetHttpConnectionString());
        await administrationClient.GetNamespacePropertiesAsync(cancellationToken).ConfigureAwait(false);

        if (configStore is not null)
        {
            foreach (string identifier in dockerEnvironment.UsedServiceBusIdentifiers)
            {
                ServiceBusConfig current = configStore.GetConfig(identifier);
                configStore.AddConfig(identifier, current with { ConnectionString = connectionString });
            }
        }

        ServiceBusRuntimeState runtimeState = new(container, materializedTopology.IsTemporary ? materializedTopology.ConfigPath : null);
        dockerEnvironment.SetRuntimeState(Id, runtimeState);
        return runtimeState;
    }

    public override async Task DeconstructAsync(object? state, IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        if (state is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
    }
}