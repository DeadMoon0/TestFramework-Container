using Azure.Storage.Blobs;
using Azure.Data.Tables;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using TestFramework.Azure.Configuration;
using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;
using TestFramework.Core.Logging;
using TestFramework.Core.Variables;

namespace TestFramework.Container.AzureDocker.Components;

internal sealed class AzuriteEnvComponent : EnvComponent
{
    public override EnvComponentIdentifier Id => DockerAzureEnvironment.AzuriteComponentId;

    public override async Task<object?> CreateAsync(IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        DockerAzureEnvironment dockerEnvironment = (DockerAzureEnvironment)environment;
        IContainer container = new ContainerBuilder(dockerEnvironment.Options.AzuriteImage)
            .WithPortBinding(10000, true)
            .WithPortBinding(10001, true)
            .WithPortBinding(10002, true)
            .WithCommand("azurite", "--blobHost", "0.0.0.0", "--queueHost", "0.0.0.0", "--tableHost", "0.0.0.0", "--skipApiVersionCheck")
            .Build();

        await container.StartAsync(cancellationToken).ConfigureAwait(false);

        string connectionString = CreateConnectionString(container);
        ConnectionStringGuards.EnsureAzurite(connectionString);

        ConfigStore<StorageAccountConfig>? configStore = serviceProvider.GetService(typeof(ConfigStore<StorageAccountConfig>)) as ConfigStore<StorageAccountConfig>;
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
        if (state is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
    }

    private static string CreateConnectionString(IContainer container)
    {
        return $"DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://{container.Hostname}:{container.GetMappedPublicPort(10000)}/devstoreaccount1;QueueEndpoint=http://{container.Hostname}:{container.GetMappedPublicPort(10001)}/devstoreaccount1;TableEndpoint=http://{container.Hostname}:{container.GetMappedPublicPort(10002)}/devstoreaccount1;";
    }

}