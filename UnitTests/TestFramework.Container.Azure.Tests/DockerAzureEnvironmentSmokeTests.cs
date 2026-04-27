using Azure.Data.Tables;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Cosmos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TestFramework.Azure;
using TestFramework.Azure.Configuration;
using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Azure.DB.SqlServer;
using TestFramework.Azure.Extensions;
using TestFramework.Azure.StorageAccount.Blob;
using TestFramework.Azure.StorageAccount.Table;
using TestFramework.Container.Azure;
using TestFramework.Core.Steps.Options;
using TestFramework.Core.Timelines;
using TestFramework.Core.Timelines.Builder.TimelineBuilder;
using TestFramework.Core.Timelines.Builder.TimelineRunBuilder;
using TestFramework.Core.Variables;

namespace TestFramework.Container.Azure.Tests;

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

    //[Fact]
    //[Trait("Category", "DockerSmoke")]
    //public async Task Timeline_CanInvokeDockerHostedFunctionAppHttpEndpoint_WhenSmokeEnabled()
    //{
    //    if (!IsSmokeEnabled())
    //        return;

    //    using ServiceProvider serviceProvider = CreateAzureServiceProvider(withFunctionApp: true);
    //    DockerAzureEnvironment environment = new(new DockerAzureEnvironmentOptions
    //    {
    //        FunctionApps =
    //        [
    //            DockerFunctionAppRegistration.Create<AnalysisProcessor>("func", builder => builder
    //                .UseStorage("storage", tableNameSettingName: "StorageTableName")
    //                .UseCosmos("cosmos")
    //                .UseServiceBusReply("bus"))
    //        ],
    //    });

    //    Timeline timeline = Timeline.Create()
    //        .Trigger(
    //            AzureTF.Trigger.FunctionApp
    //                .Http("func")
    //                .SelectEndpointWithMethod<AnalysisProcessor>(nameof(AnalysisProcessor.Run))
    //                .WithBody(Var.Const("{\"runId\":\"\",\"sampleDocId\":\"\",\"analysisReplyCorrelationId\":\"\"}"))
    //                .Call())
    //        .Name("function-call")
    //        .Build();

    //    TimelineRun run = await timeline
    //        .SetupRun(serviceProvider)
    //        .SetEnv(environment)
    //        .RunAsync();

    //    run.EnsureRanToCompletion();

    //    HttpResponseMessage response = Assert.IsType<HttpResponseMessage>(run.Step("function-call").LastResult.Result);
    //    string responseBody = await response.Content.ReadAsStringAsync();
    //    Assert.True(response.StatusCode == System.Net.HttpStatusCode.InternalServerError, $"Expected InternalServerError but received {(int)response.StatusCode} {response.StatusCode}. Body: {responseBody}");
    //    Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.FunctionAppComponentId));
    //}

    private static bool IsSmokeEnabled()
    {
        return string.Equals(Environment.GetEnvironmentVariable("TESTFRAMEWORK_CONTAINER_SMOKE"), "1", StringComparison.Ordinal);
    }

    private static ServiceProvider CreateAzureServiceProvider(bool withFunctionApp = false)
    {
        ServiceCollection services = new();

        services.AddSingleton(ConfigStore<StorageAccountConfig>.Create("storage", new StorageAccountConfig
        {
            ConnectionString = "UseDevelopmentStorage=true",
            QueueContainerName = null,
            BlobContainerName = "smoke-blob",
            TableContainerName = SmokeTableName,
        }));
        services.AddSingleton(ConfigStore<CosmosContainerDbConfig>.Create("cosmos", new CosmosContainerDbConfig
        {
            ConnectionString = "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;",
            DatabaseName = "smoke-db",
            ContainerName = "smoke-container",
        }));
        services.AddSingleton(ConfigStore<SqlDatabaseConfig>.Create("sql", new SqlDatabaseConfig
        {
            ConnectionString = "Server=localhost;Database=master;User Id=sa;Password=Your_password123;TrustServerCertificate=True",
            DatabaseName = "master",
        }));
        services.AddSingleton(ConfigStore<ServiceBusConfig>.Create("bus", new ServiceBusConfig
        {
            ConnectionString = "Endpoint=sb://localhost/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=local",
            QueueName = "default-queue",
            TopicName = null,
            SubscriptionName = null,
            RequiredSession = false,
        }));

        if (withFunctionApp)
        {
            services.AddSingleton(ConfigStore<FunctionAppConfig>.Create("func", new FunctionAppConfig
            {
                BaseUrl = "http://localhost/",
                Code = "local-test-key",
            }));
        }

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