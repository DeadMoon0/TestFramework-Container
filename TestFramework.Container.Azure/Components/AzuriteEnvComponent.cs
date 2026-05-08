using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using TestFramework.Azure.Configuration;
using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;
using TestFramework.Core.Logging;
using TestFramework.Core.Variables;

namespace TestFramework.Container.Azure.Components;

internal sealed class AzuriteEnvComponent : DockerAzureEnvComponent
{
    public override EnvComponentIdentifier Id => DockerAzureEnvironment.AzuriteComponentId;

    public override IReadOnlyList<EnvComponentIdentifier> Dependencies => [DockerAzureEnvironment.NetworkComponentId];

    public override async Task<object?> CreateAsync(IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        DockerAzureEnvironment dockerEnvironment = GetDockerEnvironment(environment);
        ConfigStore<StorageAccountConfig>? configStore = EnvComponentConfigStoreGuard.GetRequiredStore<StorageAccountConfig>(dockerEnvironment, serviceProvider, dockerEnvironment.UsedStorageIdentifiers, "Azurite environment setup");
        INetwork network = dockerEnvironment.GetRequiredRuntimeState<INetwork>(DockerAzureEnvironment.NetworkComponentId);
        IContainer container = new ContainerBuilder(dockerEnvironment.GetAzuriteImage())
            .WithNetwork(network)
            .WithNetworkAliases(DockerAzureEnvironment.AzuriteNetworkAlias)
            .WithPortBinding(10000, true)
            .WithPortBinding(10001, true)
            .WithPortBinding(10002, true)
            .WithCommand("azurite", "--blobHost", "0.0.0.0", "--queueHost", "0.0.0.0", "--tableHost", "0.0.0.0", "--skipApiVersionCheck")
            .Build();

        await container.StartAsync(cancellationToken).ConfigureAwait(false);

        string connectionString = dockerEnvironment.GetEndpointMap().CreateAzuriteConnectionString(container);
        ConnectionStringGuards.EnsureAzurite(connectionString);

        if (configStore is not null)
        {
            foreach (string identifier in dockerEnvironment.UsedStorageIdentifiers)
            {
                StorageAccountConfig current = configStore.GetConfig(identifier);
                configStore.AddConfig(identifier, current with { ConnectionString = connectionString });
            }
        }

        dockerEnvironment.SetRuntimeState(Id, container);
        return container;
    }

    public override async Task DeconstructAsync(object? state, IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        if (state is IContainer container)
        {
            await ForceRemoveContainerAsync(container, cancellationToken).ConfigureAwait(false);
        }
        else if (state is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
    }
}