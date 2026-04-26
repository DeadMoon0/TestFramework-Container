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

internal sealed class ServiceBusEnvComponent : EnvComponent
{
    public override EnvComponentIdentifier Id => DockerAzureEnvironment.ServiceBusComponentId;

    public override IReadOnlyList<EnvComponentIdentifier> Dependencies => [DockerAzureEnvironment.NetworkComponentId, DockerAzureEnvironment.MsSqlComponentId];

    public override async Task<object?> CreateAsync(IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        DockerAzureEnvironment dockerEnvironment = (DockerAzureEnvironment)environment;
        INetwork network = dockerEnvironment.GetRequiredRuntimeState<INetwork>(DockerAzureEnvironment.NetworkComponentId);
        MsSqlContainer msSqlContainer = dockerEnvironment.GetRequiredRuntimeState<MsSqlContainer>(DockerAzureEnvironment.MsSqlComponentId);
        string configPath = ServiceBusConfigLocator.Resolve(dockerEnvironment.Options.ServiceBusTopologyConfigPath);

        ServiceBusContainer container = new ServiceBusBuilder(dockerEnvironment.Options.ServiceBusImage)
            .WithAcceptLicenseAgreement(true)
            .WithMsSqlContainer(network, msSqlContainer, ServiceBusBuilder.DatabaseNetworkAlias, dockerEnvironment.Options.MsSqlPassword)
            .WithConfig(configPath)
            .WithNetworkAliases(DockerAzureEnvironment.ServiceBusNetworkAlias)
            .Build();

        await container.StartAsync(cancellationToken).ConfigureAwait(false);

        string connectionString = container.GetConnectionString();
        ConnectionStringGuards.EnsureServiceBus(connectionString);

        ServiceBusAdministrationClient administrationClient = new(container.GetHttpConnectionString());
        await administrationClient.GetNamespacePropertiesAsync(cancellationToken).ConfigureAwait(false);

        ConfigStore<ServiceBusConfig>? configStore = serviceProvider.GetService(typeof(ConfigStore<ServiceBusConfig>)) as ConfigStore<ServiceBusConfig>;
        if (configStore is not null)
        {
            foreach (string identifier in dockerEnvironment.UsedServiceBusIdentifiers)
            {
                ServiceBusConfig current = configStore.GetConfig(identifier);
                configStore.AddConfig(identifier, current with { ConnectionString = connectionString });
            }
        }

        dockerEnvironment.SetRuntimeState(Id, container);
        return container;
    }

    public override async Task DeconstructAsync(object? state, IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        if (state is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
    }
}