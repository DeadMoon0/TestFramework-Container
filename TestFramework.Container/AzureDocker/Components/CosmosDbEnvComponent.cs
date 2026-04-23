using Microsoft.Azure.Cosmos;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using System.Diagnostics;
using System.IO;
using TestFramework.Azure.Configuration;
using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;
using TestFramework.Core.Logging;
using TestFramework.Core.Variables;

namespace TestFramework.Container.AzureDocker.Components;

internal sealed class CosmosDbEnvComponent : EnvComponent
{
    private static readonly string DebugLogPath = Path.Combine(AppContext.BaseDirectory, "cosmos-env-debug.log");

    public override EnvComponentIdentifier Id => DockerAzureEnvironment.CosmosDbComponentId;

    public override async Task<object?> CreateAsync(IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        DockerAzureEnvironment dockerEnvironment = (DockerAzureEnvironment)environment;
        ContainerBuilder builder = new ContainerBuilder(dockerEnvironment.Options.CosmosDbImage)
            .WithPortBinding(8080, true)
            .WithPortBinding(8081, true)
            .WithPortBinding(1234, true);

        if (dockerEnvironment.Options.CosmosDbImage.Contains("vnext-preview", StringComparison.OrdinalIgnoreCase))
            builder = builder.WithCommand("--protocol", "https");

        IContainer container = builder.Build();

        await container.StartAsync(cancellationToken).ConfigureAwait(false);

        string connectionString = CreateConnectionString(container);
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

        ConfigStore<CosmosContainerDbConfig>? configStore = serviceProvider.GetService(typeof(ConfigStore<CosmosContainerDbConfig>)) as ConfigStore<CosmosContainerDbConfig>;
        if (configStore is not null)
        {
            foreach (string identifier in dockerEnvironment.UsedCosmosIdentifiers)
            {
                CosmosContainerDbConfig current = configStore.GetConfig(identifier);
                CosmosContainerDbConfig updated = current with { ConnectionString = connectionString };
                configStore.AddConfig(identifier, updated);

                if (!string.IsNullOrWhiteSpace(updated.PartitionKeyPath))
                {
                    LogDebug($"Deploying Cosmos schema for '{identifier}': {updated.DatabaseName}/{updated.ContainerName} ({updated.PartitionKeyPath})");
                    logger.LogInformation($"Deploying Cosmos schema for '{identifier}': {updated.DatabaseName}/{updated.ContainerName} ({updated.PartitionKeyPath})");
                    await DeploySchemaAsync(client, updated, logger, cancellationToken).ConfigureAwait(false);
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

    private static string CreateConnectionString(IContainer container)
    {
        return $"AccountEndpoint=https://{container.Hostname}:{container.GetMappedPublicPort(8081)}/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;";
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

    private static async Task DeploySchemaAsync(CosmosClient client, CosmosContainerDbConfig config, ScopedLogger logger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            LogDebug($"Cosmos schema step start: CreateDatabaseIfNotExistsAsync('{config.DatabaseName}')");
            logger.LogInformation($"Cosmos schema step start: CreateDatabaseIfNotExistsAsync('{config.DatabaseName}')");
            Database database = await client.CreateDatabaseIfNotExistsAsync(config.DatabaseName, throughput: 400).ConfigureAwait(false);
            LogDebug($"Cosmos schema step complete: CreateDatabaseIfNotExistsAsync('{config.DatabaseName}') in {stopwatch.Elapsed}.");
            logger.LogInformation($"Cosmos schema step complete: CreateDatabaseIfNotExistsAsync('{config.DatabaseName}') in {stopwatch.Elapsed}.");

            cancellationToken.ThrowIfCancellationRequested();

            stopwatch.Restart();
            LogDebug($"Cosmos schema step start: CreateContainerIfNotExistsAsync('{config.ContainerName}', '{config.PartitionKeyPath}')");
            logger.LogInformation($"Cosmos schema step start: CreateContainerIfNotExistsAsync('{config.ContainerName}', '{config.PartitionKeyPath}')");
            await database.CreateContainerIfNotExistsAsync(config.ContainerName, config.PartitionKeyPath!, throughput: 400).ConfigureAwait(false);
            LogDebug($"Cosmos schema step complete: CreateContainerIfNotExistsAsync('{config.ContainerName}') in {stopwatch.Elapsed}.");
            logger.LogInformation($"Cosmos schema step complete: CreateContainerIfNotExistsAsync('{config.ContainerName}') in {stopwatch.Elapsed}.");
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