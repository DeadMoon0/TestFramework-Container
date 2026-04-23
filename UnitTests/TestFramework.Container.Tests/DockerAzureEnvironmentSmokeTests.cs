using TestFramework.Container.AzureDocker;
using TestFramework.Azure;
using TestFramework.Azure.Configuration;
using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Azure.DB.CosmosDB;
using TestFramework.Azure.DB.SqlServer;
using TestFramework.Azure.Extensions;
using TestFramework.Azure.ServiceBus;
using TestFramework.Azure.StorageAccount.Blob;
using TestFramework.Azure.StorageAccount.Table;
using TestFramework.Core.Environment;
using TestFramework.Core.Logging;
using TestFramework.Core.Steps;
using TestFramework.Core.Steps.Options;
using TestFramework.Core.Timelines;
using TestFramework.Core.Timelines.Builder.TimelineBuilder;
using TestFramework.Core.Timelines.Builder.TimelineRunBuilder;
using TestFramework.Core.Variables;
using Azure.Messaging.ServiceBus;
using Azure.Data.Tables;
using Microsoft.Azure.Cosmos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http;

namespace TestFramework.Container.Tests;

public class DockerAzureEnvironmentSmokeTests
{
    private const string SmokeTableName = "smoketable";

    [Fact]
    [Trait("Category", "DockerSmoke")]
    public async Task Timeline_CanRunBlobIsLiveWithDockerAzureEnvironment_WhenSmokeEnabled()
    {
        if (!IsSmokeEnabled())
            return;

        using ServiceProvider serviceProvider = CreateAzureServiceProvider();
        TimelineRun run = await RunSmokeTimelineAsync(
            serviceProvider,
            [builder => builder.SetupArtifact("blob").WithRetry(5, CalcDelays.Fixed(TimeSpan.FromSeconds(2))).Trigger(AzureTF.Trigger.IsLive.Blob("storage", AlivenessLevel.Resource))],
            runBuilder => runBuilder.AddArtifact("blob", new StorageAccountBlobArtifactReference("storage", Var.Const("smoke-blob.txt")), new StorageAccountBlobArtifactData([1, 2, 3], new Dictionary<string, string>())));

        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.AzuriteComponentId));
    }

    [Fact]
    [Trait("Category", "DockerSmoke")]
    public async Task Timeline_CanRunTableIsLiveWithDockerAzureEnvironment_WhenSmokeEnabled()
    {
        if (!IsSmokeEnabled())
            return;

        using ServiceProvider serviceProvider = CreateAzureServiceProvider();
        TimelineRun run = await RunSmokeTimelineAsync(
            serviceProvider,
            [builder => builder.SetupArtifact("table").Trigger(AzureTF.Trigger.IsLive.Table("storage", AlivenessLevel.Resource)).WithTimeOut(TimeSpan.FromMinutes(1))],
            runBuilder => runBuilder.AddArtifact(
                "table",
                    new TableStorageEntityArtifactReference<SmokeTableEntity>("storage", Var.Const(SmokeTableName), Var.Const("pk"), Var.Const("rk")),
                new TableStorageEntityArtifactData<SmokeTableEntity>(new SmokeTableEntity())));

        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.AzuriteComponentId));
    }

    [Fact]
    [Trait("Category", "DockerSmoke")]
    public async Task Timeline_CanRunCosmosIsLiveWithDockerAzureEnvironment_WhenSmokeEnabled()
    {
        if (!IsSmokeEnabled())
            return;

        using ServiceProvider serviceProvider = CreateAzureServiceProvider();
        TimelineRun run = await RunSmokeTimelineAsync(
            serviceProvider,
            [builder => builder.Trigger(AzureTF.Trigger.IsLive.Cosmos("cosmos", AlivenessLevel.Authenticated)).WithTimeOut(TimeSpan.FromMinutes(2))]);

        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.CosmosDbComponentId));
    }

    [Fact]
    [Trait("Category", "DockerSmoke")]
    public async Task Timeline_CanRunSqlIsLiveWithDockerAzureEnvironment_WhenSmokeEnabled()
    {
        if (!IsSmokeEnabled())
            return;

        using ServiceProvider serviceProvider = CreateAzureServiceProvider();
        TimelineRun run = await RunSmokeTimelineAsync(
            serviceProvider,
            [builder => builder.Trigger(AzureTF.Trigger.IsLive.Sql("sql"))]);

        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.MsSqlComponentId));
    }

    [Fact]
    [Trait("Category", "DockerSmoke")]
    public async Task Timeline_CanRunServiceBusStepsWithDockerAzureEnvironment_WhenSmokeEnabled()
    {
        if (!IsSmokeEnabled())
            return;

        using ServiceProvider serviceProvider = CreateAzureServiceProvider();
        TimelineRun run = await RunSmokeTimelineAsync(
            serviceProvider,
            [builder => builder.Trigger(AzureTF.Trigger.ServiceBus.Send("bus", Var.Const(new ServiceBusMessage("payload"))))]);

        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.ServiceBusComponentId));
        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.MsSqlComponentId));
    }

    [Fact]
    [Trait("Category", "DockerSmoke")]
    public async Task Timeline_CanRunAllAzureStepsAcrossDockerAzureComponents_WhenSmokeEnabled()
    {
        if (!IsSmokeEnabled())
            return;

        using ServiceProvider serviceProvider = CreateAzureServiceProvider();
        TimelineRun run = await RunSmokeTimelineAsync(
            serviceProvider,
            [
                builder => builder.SetupArtifact("blob").WithRetry(5, CalcDelays.Fixed(TimeSpan.FromSeconds(2))).Trigger(AzureTF.Trigger.IsLive.Blob("storage", AlivenessLevel.Resource)),
                builder => builder.SetupArtifact("table").Trigger(AzureTF.Trigger.IsLive.Table("storage", AlivenessLevel.Resource)).WithTimeOut(TimeSpan.FromMinutes(1)),
                builder => builder.Trigger(AzureTF.Trigger.IsLive.Cosmos("cosmos", AlivenessLevel.Authenticated)).WithTimeOut(TimeSpan.FromMinutes(2)),
                builder => builder.Trigger(AzureTF.Trigger.IsLive.Sql("sql")),
                builder => builder.Trigger(AzureTF.Trigger.ServiceBus.Send("bus", Var.Const(new ServiceBusMessage("payload"))))
            ],
            runBuilder => runBuilder
                .AddArtifact("blob", new StorageAccountBlobArtifactReference("storage", Var.Const("smoke-blob.txt")), new StorageAccountBlobArtifactData([1, 2, 3], new Dictionary<string, string>()))
                    .AddArtifact("table", new TableStorageEntityArtifactReference<SmokeTableEntity>("storage", Var.Const(SmokeTableName), Var.Const("pk"), Var.Const("rk")), new TableStorageEntityArtifactData<SmokeTableEntity>(new SmokeTableEntity())));

        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.AzuriteComponentId));
        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.CosmosDbComponentId));
        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.MsSqlComponentId));
        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.ServiceBusComponentId));
    }

    private static bool IsSmokeEnabled()
    {
        return string.Equals(Environment.GetEnvironmentVariable("TESTFRAMEWORK_CONTAINER_SMOKE"), "1", StringComparison.Ordinal);
    }

    private static ServiceProvider CreateAzureServiceProvider()
    {
        ServiceCollection services = new();

        services.AddSingleton(CreateStore("storage", new StorageAccountConfig
        {
            ConnectionString = "UseDevelopmentStorage=true",
            QueueContainerName = null,
            BlobContainerName = "smoke-blob",
            TableContainerName = SmokeTableName,
        }));
        services.AddSingleton(CreateStore("cosmos", new CosmosContainerDbConfig
        {
            ConnectionString = "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;",
            DatabaseName = "smoke-db",
            ContainerName = "smoke-container",
            PartitionKeyPath = "/PartitionKey",
        }));
        services.AddSingleton(CreateStore("sql", new SqlDatabaseConfig
        {
            ConnectionString = "Server=localhost;Database=master;User Id=sa;Password=Your_password123;TrustServerCertificate=True",
            DatabaseName = "master",
        }));
        services.AddSingleton(CreateStore("bus", new ServiceBusConfig
        {
            ConnectionString = "Endpoint=sb://localhost/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=local",
            QueueName = "default-queue",
            TopicName = null,
            SubscriptionName = null,
            RequiredSession = false,
        }));

        services.ConfigureCosmosClientOptions(_ => new CosmosClientOptions
        {
            HttpClientFactory = () => new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            }),
        });

        services.AddDbContext<SmokeSqlDbContext>((serviceProvider, options) =>
        {
            SqlDatabaseConfig config = serviceProvider.GetRequiredService<ConfigStore<SqlDatabaseConfig>>().GetConfig("sql");
            options.UseSqlServer(config.ConnectionString);
        });
        services.AddSqlArtifactContexts(registry => registry.AddForIdentifier<SmokeSqlDbContext>("sql"));

        return services.BuildServiceProvider();
    }

    private static ConfigStore<TConfig> CreateStore<TConfig>(string identifier, TConfig config)
    {
        ConfigStore<TConfig> store = new();
        store.AddConfig(identifier, config);
        return store;
    }

    private static async Task<TimelineRun> RunSmokeTimelineAsync(
        IServiceProvider serviceProvider,
        IReadOnlyList<Func<ITimelineBuilder, ITimelineBuilderModifier>> configureSteps,
        Func<ITimelineRunBuilder, ITimelineRunBuilder>? configureRunBuilder = null)
    {
        DockerAzureEnvironment environment = new();
        ITimelineBuilder builder = Timeline.Create();
        foreach (Func<ITimelineBuilder, ITimelineBuilderModifier> configureStep in configureSteps)
            builder = configureStep(builder);

        Timeline timeline = builder.Build();
        ITimelineRunBuilder runBuilder = timeline.SetupRun(serviceProvider);
        if (configureRunBuilder is not null)
            runBuilder = configureRunBuilder(runBuilder);

        TimelineRun run = await runBuilder
            .SetEnv(environment)
            .RunAsync();

        run.EnsureRanToCompletion();

        return run;
    }

    private sealed class SmokeSqlDbContext(DbContextOptions<SmokeSqlDbContext> options) : DbContext(options);

    private sealed class SmokeTableEntity : ITableEntity
    {
        public string PartitionKey { get; set; } = "pk";
        public string RowKey { get; set; } = "rk";
        public DateTimeOffset? Timestamp { get; set; }
        public global::Azure.ETag ETag { get; set; }
    }
}