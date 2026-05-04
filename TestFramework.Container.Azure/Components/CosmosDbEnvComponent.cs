using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Azure.Cosmos;
using System.Diagnostics;
using TestFramework.Azure.Configuration;
using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Azure.DB.CosmosDB;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;
using TestFramework.Core.Logging;
using TestFramework.Core.Variables;

namespace TestFramework.Container.Azure.Components;

internal sealed class CosmosDbEnvComponent : DockerAzureEnvComponent
{
    private static readonly string DebugLogPath = Path.Combine(AppContext.BaseDirectory, "cosmos-env-debug.log");

    public override EnvComponentIdentifier Id => DockerAzureEnvironment.CosmosDbComponentId;

    public override IReadOnlyList<EnvComponentIdentifier> Dependencies => [DockerAzureEnvironment.NetworkComponentId];

    public override async Task<object?> CreateAsync(IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        DockerAzureEnvironment dockerEnvironment = GetDockerEnvironment(environment);
        ConfigStore<CosmosContainerDbConfig>? configStore = EnvComponentConfigStoreGuard.GetRequiredStore<CosmosContainerDbConfig>(dockerEnvironment, serviceProvider, dockerEnvironment.UsedCosmosIdentifiers, "Cosmos environment setup");
        INetwork network = dockerEnvironment.GetRequiredRuntimeState<INetwork>(DockerAzureEnvironment.NetworkComponentId);
        string cosmosImage = dockerEnvironment.GetCosmosDbImage();
        ContainerBuilder builder = new ContainerBuilder(cosmosImage)
            .WithNetwork(network)
            .WithNetworkAliases(DockerAzureEnvironment.CosmosDbNetworkAlias)
            .WithPortBinding(8080, true)
            .WithPortBinding(8081, true)
            .WithPortBinding(1234, true);

        if (cosmosImage.Contains("vnext-preview", StringComparison.OrdinalIgnoreCase))
            builder = builder.WithCommand("--protocol", "https");

        IContainer container = builder.Build();

        await container.StartAsync(cancellationToken).ConfigureAwait(false);

        string connectionString = dockerEnvironment.GetEndpointMap().CreateCosmosConnectionString(container);
        ConnectionStringGuards.EnsureCosmos(connectionString);

        using CosmosClient client = new(connectionString, new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            HttpClientFactory = () => new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            }),
        });
        LogDebug($"Waiting for Cosmos gateway: {connectionString}");
        logger.LogInformation($"Waiting for Cosmos gateway: {connectionString}");
        await WaitForGatewayAsync(client, logger, cancellationToken).ConfigureAwait(false);
        LogDebug("Cosmos gateway is ready.");
        logger.LogInformation("Cosmos gateway is ready.");

        if (configStore is not null)
        {
            foreach (string identifier in dockerEnvironment.UsedCosmosIdentifiers)
            {
                CosmosContainerDbConfig current = configStore.GetConfig(identifier);
                CosmosContainerDbConfig updated = current with { ConnectionString = connectionString };
                configStore.AddConfig(identifier, updated);

                if (dockerEnvironment.CosmosPartitionKeyPaths.TryGetValue(identifier, out string? partitionKeyPath))
                {
                    LogDebug($"Deploying Cosmos schema for '{identifier}': {updated.DatabaseName}/{updated.ContainerName} ({partitionKeyPath})");
                    logger.LogInformation($"Deploying Cosmos schema for '{identifier}': {updated.DatabaseName}/{updated.ContainerName} ({partitionKeyPath})");
                    await DeploySchemaAsync(updated.ConnectionString, updated, partitionKeyPath, logger, cancellationToken).ConfigureAwait(false);
                    LogDebug($"Finished Cosmos schema deployment for '{identifier}'.");
                    logger.LogInformation($"Finished Cosmos schema deployment for '{identifier}'.");
                }
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
    private static async Task WaitForGatewayAsync(CosmosClient client, ScopedLogger logger, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.AddMinutes(2);
        Exception? lastError = null;
        int attempt = 0;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;
            try
            {
                LogDebug($"Cosmos gateway readiness attempt {attempt}.");
                logger.LogInformation($"Cosmos gateway readiness attempt {attempt}.");
                await client.ReadAccountAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception exception)
            {
                lastError = exception;
                LogDebug($"Cosmos gateway readiness attempt {attempt} failed: {exception.GetType().Name}: {exception.Message}");
                logger.LogInformation($"Cosmos gateway readiness attempt {attempt} failed: {exception.GetType().Name}: {exception.Message}");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new TimeoutException("The Cosmos emulator gateway did not become ready within two minutes.", lastError);
    }

    private static async Task DeploySchemaAsync(string connectionString, CosmosContainerDbConfig config, string partitionKeyPath, ScopedLogger logger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            LogDebug($"Cosmos schema step start: CreateDatabaseIfNotExistsAsync('{config.DatabaseName}')");
            logger.LogInformation($"Cosmos schema step start: CreateDatabaseIfNotExistsAsync('{config.DatabaseName}')");
            await CosmosSchemaRestClient.EnsureDatabaseAndContainerExistAsync(connectionString, config.DatabaseName, config.ContainerName, partitionKeyPath, cancellationToken).ConfigureAwait(false);
            LogDebug($"Cosmos schema step complete: CreateDatabaseIfNotExistsAsync('{config.DatabaseName}') in {stopwatch.Elapsed}.");
            logger.LogInformation($"Cosmos schema step complete: CreateDatabaseIfNotExistsAsync('{config.DatabaseName}') in {stopwatch.Elapsed}.");
        }
        catch (Exception exception)
        {
            LogDebug($"Cosmos schema deployment failed after {stopwatch.Elapsed}: {exception.GetType().Name}: {exception.Message}");
            logger.LogInformation($"Cosmos schema deployment failed after {stopwatch.Elapsed}: {exception.GetType().Name}: {exception.Message}");
            throw;
        }
    }

    private static void LogDebug(string message)
    {
        try
        {
            File.AppendAllText(DebugLogPath, $"{DateTime.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}