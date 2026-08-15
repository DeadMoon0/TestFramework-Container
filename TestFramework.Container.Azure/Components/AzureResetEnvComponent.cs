using Azure.Data.Tables;
using Azure.Data.Tables.Models;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Azure.Configuration;
using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Azure.DB.CosmosDB;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;
using TestFramework.Core.Logging;
using TestFramework.Core.Variables;

namespace TestFramework.Container.Azure.Components;

/// <summary>
/// Empties the emulators a previous run left behind, before this run puts anything into them.
/// </summary>
/// <remarks>
/// <para>
/// The four emulator components are persistent, and in a hosted collection the persistent context hands
/// back their cached state instead of calling their <c>CreateAsync</c> again. Nothing they do at startup
/// therefore happens a second time, so there is no place inside them where a per-run reset could live.
/// This component is that place: per-run, so it runs every time, and depending on the persistent
/// components, so it runs after them. The reverse — a persistent component depending on a per-run one —
/// is what <c>ValidatePersistentClosure</c> forbids.
/// </para>
/// <para>
/// It only touches what the environment declared. Reading the configuration stores rather than
/// enumerating the emulators keeps a purge from reaching anything the run does not own, and keeps it
/// well away from the database the Service Bus emulator keeps its own state in — dropping that ends the
/// emulator, and with it the rest of the suite.
/// </para>
/// </remarks>
internal sealed class AzureResetEnvComponent(DockerAzureEnvironment owner) : DockerAzureEnvComponent
{
    private const int DrainBatchSize = 100;
    private const int MaxDrainBatches = 200;
    private static readonly TimeSpan DrainWait = TimeSpan.FromSeconds(1);

    public override EnvComponentIdentifier Id => DockerAzureEnvironment.AzureResetComponentId;

    /// <summary>
    /// Runs on every run, which is the entire point of it.
    /// </summary>
    public override EnvComponentReuseMode ReuseMode => EnvComponentReuseMode.PerRun;

    /// <summary>
    /// The emulators that resolved, so the purge runs once each of them is up.
    /// </summary>
    /// <remarks>
    /// Derived from resolution state, so it must not be read before
    /// <see cref="DockerAzureEnvironment.ResolveComponents"/> has run.
    /// </remarks>
    public override IReadOnlyList<EnvComponentIdentifier> Dependencies => owner.GetResetComponentDependencies();

    public override async Task<object?> CreateAsync(IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        DockerAzureEnvironment dockerEnvironment = GetDockerEnvironment(environment);

        if (dockerEnvironment.GetResetMode() == AzureResetMode.None)
        {
            logger.LogInformation($"Skipping the Azure reset because the environment is set to {nameof(AzureResetMode)}.{nameof(AzureResetMode.None)}.");
            return null;
        }

        // A run that started the emulators itself has nothing to purge: they came up empty seconds ago.
        // Only a run handed containers a previous run already used pays for this.
        if (!dockerEnvironment.HasReusedPersistentComponents)
        {
            logger.LogInformation("Skipping the Azure reset because this run started its own emulators, so there is nothing a previous run could have left behind.");
            return null;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        logger.LogInformation("Purging the declared Azure resources so this run starts from an empty environment.");

        await PurgeStorageAsync(dockerEnvironment, serviceProvider, logger, cancellationToken).ConfigureAwait(false);
        await PurgeCosmosAsync(dockerEnvironment, serviceProvider, logger, cancellationToken).ConfigureAwait(false);
        await PurgeServiceBusAsync(dockerEnvironment, serviceProvider, logger, cancellationToken).ConfigureAwait(false);
        await PurgeSqlAsync(dockerEnvironment, serviceProvider, logger, cancellationToken).ConfigureAwait(false);

        logger.LogInformation($"Finished purging the declared Azure resources in {stopwatch.Elapsed:g}.");
        return null;
    }

    public override Task DeconstructAsync(object? state, IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        // The purge owns nothing, so there is nothing to give back.
        return Task.CompletedTask;
    }

    private static async Task PurgeStorageAsync(DockerAzureEnvironment dockerEnvironment, IServiceProvider serviceProvider, ScopedLogger logger, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<string> identifiers = dockerEnvironment.UsedStorageIdentifiers;
        if (identifiers.Count == 0)
            return;

        ConfigStore<StorageAccountConfig>? store = dockerEnvironment.GetOrCreateConfigStore<StorageAccountConfig>(serviceProvider, identifiers, "Azure reset");
        if (store is null)
            return;

        // Several identifiers share the one emulator account, and one pass over it clears them all.
        foreach (string connectionString in DistinctConnectionStrings(identifiers, identifier => store.GetConfig(identifier).ConnectionString))
        {
            await PurgeBlobContainersAsync(connectionString, logger, cancellationToken).ConfigureAwait(false);
            await PurgeTablesAsync(connectionString, logger, cancellationToken).ConfigureAwait(false);
            await PurgeQueuesAsync(connectionString, logger, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task PurgeBlobContainersAsync(string connectionString, ScopedLogger logger, CancellationToken cancellationToken)
    {
        BlobServiceClient serviceClient = new(connectionString);
        List<string> names = [];
        await foreach (BlobContainerItem item in serviceClient.GetBlobContainersAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            names.Add(item.Name);

        foreach (string name in names.OrderBy(x => x, StringComparer.Ordinal))
            await serviceClient.DeleteBlobContainerAsync(name, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (names.Count > 0)
            logger.LogInformation($"Deleted {names.Count} blob container(s): {string.Join(", ", names.OrderBy(x => x, StringComparer.Ordinal))}.");
    }

    private static async Task PurgeTablesAsync(string connectionString, ScopedLogger logger, CancellationToken cancellationToken)
    {
        TableServiceClient serviceClient = new(connectionString);
        List<string> names = [];
        await foreach (TableItem item in serviceClient.QueryAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            names.Add(item.Name);

        foreach (string name in names.OrderBy(x => x, StringComparer.Ordinal))
            await serviceClient.DeleteTableAsync(name, cancellationToken).ConfigureAwait(false);

        if (names.Count > 0)
            logger.LogInformation($"Deleted {names.Count} table(s): {string.Join(", ", names.OrderBy(x => x, StringComparer.Ordinal))}.");
    }

    private static async Task PurgeQueuesAsync(string connectionString, ScopedLogger logger, CancellationToken cancellationToken)
    {
        // A queue is deleted lazily by the service and its name stays unusable for a while afterwards,
        // so the messages go and the queue stays.
        QueueServiceClient serviceClient = new(connectionString);
        List<string> names = [];
        await foreach (QueueItem item in serviceClient.GetQueuesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            names.Add(item.Name);

        foreach (string name in names.OrderBy(x => x, StringComparer.Ordinal))
            await serviceClient.GetQueueClient(name).ClearMessagesAsync(cancellationToken).ConfigureAwait(false);

        if (names.Count > 0)
            logger.LogInformation($"Cleared {names.Count} storage queue(s): {string.Join(", ", names.OrderBy(x => x, StringComparer.Ordinal))}.");
    }

    private static async Task PurgeCosmosAsync(DockerAzureEnvironment dockerEnvironment, IServiceProvider serviceProvider, ScopedLogger logger, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<string> identifiers = dockerEnvironment.UsedCosmosIdentifiers;
        if (identifiers.Count == 0)
            return;

        ConfigStore<CosmosContainerDbConfig>? store = dockerEnvironment.GetOrCreateConfigStore<CosmosContainerDbConfig>(serviceProvider, identifiers, "Azure reset");
        if (store is null)
            return;

        foreach (string identifier in identifiers.OrderBy(x => x, StringComparer.Ordinal))
        {
            CosmosContainerDbConfig config = store.GetConfig(identifier);
            if (!dockerEnvironment.CosmosPartitionKeyPaths.TryGetValue(identifier, out string? partitionKeyPath))
            {
                logger.LogWarning($"Cosmos identifier '{identifier}' has no recorded partition key path, so its container was left as it is.");
                continue;
            }

            // Dropping the container and putting it back beats deleting items one at a time by a wide
            // margin, and it puts the schema back through the path that created it in the first place.
            await DeleteCosmosContainerAsync(config, logger, cancellationToken).ConfigureAwait(false);
            await CosmosSchemaRestClient.EnsureDatabaseAndContainerExistAsync(config.ConnectionString, config.DatabaseName, config.ContainerName, partitionKeyPath, cancellationToken).ConfigureAwait(false);
            logger.LogInformation($"Recreated the Cosmos container for '{identifier}': {config.DatabaseName}/{config.ContainerName}.");
        }
    }

    private static async Task DeleteCosmosContainerAsync(CosmosContainerDbConfig config, ScopedLogger logger, CancellationToken cancellationToken)
    {
        using CosmosClient client = new(config.ConnectionString, new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            HttpClientFactory = () => new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            }),
        });

        try
        {
            await client.GetContainer(config.DatabaseName, config.ContainerName).DeleteContainerAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            // Nothing to purge is a perfectly good outcome.
            logger.LogInformation($"The Cosmos container '{config.DatabaseName}/{config.ContainerName}' did not exist yet.");
        }
    }

    private static async Task PurgeServiceBusAsync(DockerAzureEnvironment dockerEnvironment, IServiceProvider serviceProvider, ScopedLogger logger, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<string> identifiers = dockerEnvironment.UsedServiceBusIdentifiers;
        if (identifiers.Count == 0)
            return;

        ConfigStore<ServiceBusConfig>? store = dockerEnvironment.GetOrCreateConfigStore<ServiceBusConfig>(serviceProvider, identifiers, "Azure reset");
        if (store is null)
            return;

        // The emulator builds its topology from the config file it was started with, so deleting and
        // recreating an entity would leave it with a topology it never agreed to. Messages go, the
        // entities stay.
        foreach (string identifier in identifiers.OrderBy(x => x, StringComparer.Ordinal))
        {
            ServiceBusConfig config = store.GetConfig(identifier);
            await using ServiceBusClient client = new(config.ConnectionString);

            if (config.IsQueue)
            {
                await DrainAsync(client, config.QueueName!, subscriptionName: null, identifier, logger, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (config.IsTopic && !string.IsNullOrEmpty(config.SubscriptionName))
            {
                await DrainAsync(client, config.TopicName!, config.SubscriptionName, identifier, logger, cancellationToken).ConfigureAwait(false);
                continue;
            }

            logger.LogWarning($"Service Bus identifier '{identifier}' names neither a queue nor a topic subscription, so nothing was drained for it.");
        }
    }

    private static async Task DrainAsync(ServiceBusClient client, string entityName, string? subscriptionName, string identifier, ScopedLogger logger, CancellationToken cancellationToken)
    {
        // The administration client can create and delete entities but cannot empty one, so the only
        // way to purge is to be a consumer that throws everything away.
        int active = await DrainReceiverAsync(client, entityName, subscriptionName, SubQueue.None, cancellationToken).ConfigureAwait(false);
        int deadLettered = await DrainReceiverAsync(client, entityName, subscriptionName, SubQueue.DeadLetter, cancellationToken).ConfigureAwait(false);

        if (active > 0 || deadLettered > 0)
        {
            string path = subscriptionName is null ? entityName : $"{entityName}/{subscriptionName}";
            logger.LogInformation($"Drained {active} message(s) and {deadLettered} dead-lettered message(s) from '{path}' for Service Bus identifier '{identifier}'.");
        }
    }

    private static async Task<int> DrainReceiverAsync(ServiceBusClient client, string entityName, string? subscriptionName, SubQueue subQueue, CancellationToken cancellationToken)
    {
        ServiceBusReceiverOptions options = new()
        {
            ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete,
            SubQueue = subQueue,
        };

        await using ServiceBusReceiver receiver = subscriptionName is null
            ? client.CreateReceiver(entityName, options)
            : client.CreateReceiver(entityName, subscriptionName, options);

        int drained = 0;
        for (int batch = 0; batch < MaxDrainBatches; batch++)
        {
            IReadOnlyList<ServiceBusReceivedMessage> messages = await receiver
                .ReceiveMessagesAsync(DrainBatchSize, DrainWait, cancellationToken)
                .ConfigureAwait(false);

            if (messages.Count == 0)
                return drained;

            drained += messages.Count;
        }

        return drained;
    }

    private static async Task PurgeSqlAsync(DockerAzureEnvironment dockerEnvironment, IServiceProvider serviceProvider, ScopedLogger logger, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<string> identifiers = dockerEnvironment.UsedSqlIdentifiers;
        if (identifiers.Count == 0)
            return;

        ConfigStore<SqlDatabaseConfig>? store = dockerEnvironment.GetOrCreateConfigStore<SqlDatabaseConfig>(serviceProvider, identifiers, "Azure reset");
        if (store is null)
            return;

        foreach (string identifier in identifiers.OrderBy(x => x, StringComparer.Ordinal))
        {
            SqlDatabaseConfig config = store.GetConfig(identifier);
            string databaseName = config.DatabaseName;

            if (IsProtectedDatabase(databaseName))
            {
                // The Service Bus emulator keeps its own state in this server. Dropping its database
                // takes the emulator down with it, in the middle of the suite.
                logger.LogWarning($"SQL identifier '{identifier}' names the reserved database '{databaseName}', which the purge never touches.");
                continue;
            }

            string serverConnectionString = ToServerConnectionString(config.ConnectionString);
            await ExecuteAsync(
                serverConnectionString,
                $"""
                IF DB_ID(@name) IS NOT NULL
                BEGIN
                    ALTER DATABASE {QuoteName(databaseName)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE {QuoteName(databaseName)};
                END
                """,
                databaseName,
                cancellationToken).ConfigureAwait(false);

            // CREATE DATABASE refuses to share a batch, hence the wrapper.
            await ExecuteAsync(
                serverConnectionString,
                $"IF DB_ID(@name) IS NULL EXEC(N'CREATE DATABASE {QuoteName(databaseName).Replace("'", "''", StringComparison.Ordinal)};');",
                databaseName,
                cancellationToken).ConfigureAwait(false);

            logger.LogInformation($"Recreated the SQL database '{databaseName}' for identifier '{identifier}'.");
        }
    }

    /// <summary>
    /// Names the purge refuses to drop whatever a configuration says.
    /// </summary>
    private static bool IsProtectedDatabase(string databaseName)
    {
        return databaseName is "master" or "model" or "msdb" or "tempdb"
            || databaseName.StartsWith("SbEmulator", StringComparison.OrdinalIgnoreCase)
            || databaseName.Contains("ServiceBus", StringComparison.OrdinalIgnoreCase);
    }

    private static string ToServerConnectionString(string connectionString)
    {
        // Dropping a database from a connection that is inside it does not work.
        SqlConnectionStringBuilder builder = new(connectionString)
        {
            InitialCatalog = "master",
        };

        return builder.ConnectionString;
    }

    private static async Task ExecuteAsync(string connectionString, string statement, string databaseName, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = new(statement, connection);
        command.Parameters.AddWithValue("@name", databaseName);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string QuoteName(string identifier)
        => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static IEnumerable<string> DistinctConnectionStrings(IEnumerable<string> identifiers, Func<string, string> select)
        => identifiers.Select(select).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal);
}
