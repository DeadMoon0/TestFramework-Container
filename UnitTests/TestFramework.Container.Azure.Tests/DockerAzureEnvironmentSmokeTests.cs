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
using TestFramework.Azure.Identifier;
using TestFramework.Azure.StorageAccount.Blob;
using TestFramework.Azure.StorageAccount.Table;
using TestFramework.Container.Azure;
using TestFramework.Container.Azure.FunctionApp;
using TestFramework.Container.Azure.ServiceBusFunctionApp;
using TestFramework.Core.Steps.Options;
using TestFramework.Core.Timelines;
using TestFramework.Core.Timelines.Builder.TimelineBuilder;
using TestFramework.Core.Timelines.Builder.TimelineRunBuilder;
using TestFramework.Core.Variables;
using System.Net;

namespace TestFramework.Container.Azure.Tests;

// README sync note: the README golden sample is backed by the smoke test below.
// If you update that sample path or assertions, update the corresponding README sample as well.
public class DockerAzureEnvironmentSmokeTests
{
    private const string SmokeTableName = "smoketable";

    private sealed class ReadmeCosmosDefinition : DockerCosmosDefinition<SmokeTableEntity>
    {
        public override CosmosContainerIdentifier Identifier => "cosmos";
    }

    private sealed class SmokeStorageDefinition : DockerStorageDefinition
    {
        public override StorageAccountIdentifier Identifier => "storage";
    }

    private sealed class SmokeCosmosDefinition : DockerCosmosDefinition<SmokeTableEntity>
    {
        public override CosmosContainerIdentifier Identifier => "cosmos";
    }

    private sealed class SmokeServiceBusDefinition : DockerServiceBusDefinition
    {
        public override ServiceBusIdentifier Identifier => "bus";
    }

    private sealed class SmokeFunctionTriggerBusDefinition : DockerServiceBusDefinition
    {
        public override ServiceBusIdentifier Identifier => "func-trigger-bus";

        protected override void ConfigureServiceBusTopology(DockerServiceBusTopologyBuilder builder)
            => ConfigureDedicatedFunctionAppServiceBusTopology(builder);
    }

    private sealed class SmokeFunctionReplyBusDefinition : DockerServiceBusDefinition
    {
        public override ServiceBusIdentifier Identifier => "func-reply-bus";

        protected override void ConfigureServiceBusTopology(DockerServiceBusTopologyBuilder builder)
            => ConfigureDedicatedFunctionAppServiceBusTopology(builder);
    }

    private sealed class SmokeFunctionAppDefinition : DockerFunctionAppDefinition<LocalFunctionAppSmokeFunction>
    {
        public override FunctionAppIdentifier Identifier => "func";

        protected override void Configure(DockerFunctionAppBuilder builder)
        {
            builder
                .UseStorage<SmokeStorageDefinition>(tableNameSettingName: "StorageTableName")
                .UseCosmos<SmokeCosmosDefinition>()
                .UseServiceBusReply<SmokeServiceBusDefinition>();
        }
    }

    private sealed class SmokeServiceBusFunctionAppDefinition : DockerFunctionAppDefinition<LocalServiceBusFunctionAppSmokeFunction>
    {
        public override FunctionAppIdentifier Identifier => "func-sb";

        protected override void Configure(DockerFunctionAppBuilder builder)
        {
            builder
                .UseStorage<SmokeStorageDefinition>()
                .UseServiceBusTrigger<SmokeFunctionTriggerBusDefinition>()
                .UseServiceBusReply<SmokeFunctionReplyBusDefinition>();
        }
    }

    private static void ConfigureDedicatedFunctionAppServiceBusTopology(DockerServiceBusTopologyBuilder builder)
    {
        builder.AddNamespace("sbemulatorns", ns => ns
            .AddTopic("smoke-trigger-topic", topic => topic.AddSubscription("smoke-trigger-subscription"))
            .AddTopic("smoke-reply-topic", topic => topic.AddSubscription("smoke-reply-default")));
    }

    [Fact]
    [Trait("Category", "DockerSmoke")]
    public async Task PackageReadme_GoldenSample_RunsAgainstContainerBackedCosmos_WhenSmokeEnabled()
    {
        using ServiceProvider serviceProvider = CreateAzureServiceProvider();
        Timeline timeline = Timeline.Create()
            .Trigger(AzureTF.Trigger.IsLive.Cosmos("cosmos", AlivenessLevel.Authenticated)).WithTimeOut(TimeSpan.FromMinutes(2))
            .Build();

        TimelineRun run = await timeline
            .SetupRun(serviceProvider)
            .SetEnv(DockerAzureEnvironment.For<ReadmeCosmosDefinition>())
            .RunAsync();

        run.EnsureRanToCompletion();
        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.CosmosDbComponentId));
    }

    [Fact]
    [Trait("Category", "DockerSmoke")]
    public async Task Timeline_CanRunBlobIsLiveWithDockerAzureEnvironment_WhenSmokeEnabled()
    {
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

    [Fact]
    [Trait("Category", "DockerSmoke")]
    public async Task Timeline_CanInvokeDockerHostedFunctionAppHttpEndpoint_WithoutExternalDependencies()
    {
        using ServiceProvider serviceProvider = CreateAzureServiceProvider(withFunctionApp: true);
        DockerAzureEnvironment environment = DockerAzureEnvironment.For<SmokeFunctionAppDefinition>();

        Timeline timeline = Timeline.Create()
            .Trigger(
                AzureTF.Trigger.FunctionApp
                    .Http("func")
                    .SelectEndpointWithMethod<LocalFunctionAppSmokeFunction>(nameof(LocalFunctionAppSmokeFunction.Run))
                    .WithBody(Var.Const("{\"runId\":\"\",\"sampleDocId\":\"\",\"analysisReplyCorrelationId\":\"\"}"))
                    .Call())
            .Name("function-call")
            .Build();

        TimelineRun run = await timeline
            .SetupRun(serviceProvider)
            .SetEnv(environment)
            .RunAsync();

        run.EnsureRanToCompletion();

        HttpResponseMessage response = Assert.IsType<HttpResponseMessage>(run.Step("function-call").LastResult.Result);
        string responseBody = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("Local smoke function executed.", responseBody, StringComparison.Ordinal);
        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.FunctionAppComponentId));
    }

    [Fact]
    [Trait("Category", "DockerSmoke")]
    public async Task Timeline_CanInvokeDockerHostedFunctionAppServiceBusTrigger_WithDedicatedServiceBusHost()
    {
        const string correlationId = "smoke-servicebus-correlation";

        using ServiceProvider serviceProvider = CreateAzureServiceProvider();
        DockerAzureEnvironment environment = DockerAzureEnvironment.For<SmokeServiceBusFunctionAppDefinition>();

        Timeline timeline = Timeline.Create()
            .Trigger(AzureTF.Trigger.IsLive.FunctionApp("func-sb", AlivenessLevel.Reachable)).WithTimeOut(TimeSpan.FromMinutes(1)).Name("func-sb-reachable")
            .Trigger(AzureTF.Trigger.ServiceBus.Send("func-trigger-bus", Var.Const(new ServiceBusMessage(correlationId) { CorrelationId = correlationId })))
            .WaitForEvent(AzureTF.Event.ServiceBus.MessageReceived("func-reply-bus", correlationId: Var.Const(correlationId), completeMessage: Var.Const(true))).WithTimeOut(TimeSpan.FromMinutes(1)).Name("reply-received")
            .Build();

        TimelineRun run = await timeline
            .SetupRun(serviceProvider)
            .SetEnv(environment)
            .RunAsync();

        run.EnsureRanToCompletion();

        ServiceBusReceivedMessage message = Assert.IsType<ServiceBusReceivedMessage>(run.Step("reply-received").LastResult.Result);
        Assert.Equal(correlationId, message.CorrelationId);
        Assert.Equal("servicebus-smoke-processed", message.Subject);
        Assert.Equal($"processed:{correlationId}", message.Body.ToString());
        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.FunctionAppComponentId));
        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.ServiceBusComponentId));
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
        ConfigStore<ServiceBusConfig> serviceBusStore = ConfigStore<ServiceBusConfig>.Create("bus", new ServiceBusConfig
        {
            ConnectionString = "Endpoint=sb://localhost/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=local",
            QueueName = "default-queue",
            TopicName = null,
            SubscriptionName = null,
            RequiredSession = false,
        });
        serviceBusStore.AddConfig("func-trigger-bus", new ServiceBusConfig
        {
            ConnectionString = "Endpoint=sb://localhost/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=local",
            QueueName = null,
            TopicName = "smoke-trigger-topic",
            SubscriptionName = "smoke-trigger-subscription",
            RequiredSession = false,
        });
        serviceBusStore.AddConfig("func-reply-bus", new ServiceBusConfig
        {
            ConnectionString = "Endpoint=sb://localhost/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=local",
            QueueName = null,
            TopicName = "smoke-reply-topic",
            SubscriptionName = "smoke-reply-default",
            RequiredSession = false,
        });
        services.AddSingleton(serviceBusStore);

        ConfigStore<FunctionAppConfig> functionAppStore = ConfigStore<FunctionAppConfig>.Create("func-sb", new FunctionAppConfig
        {
            BaseUrl = "http://localhost/",
            Code = "local-test-key",
        });

        if (withFunctionApp)
        {
            functionAppStore.AddConfig("func", new FunctionAppConfig
            {
                BaseUrl = "http://localhost/",
                Code = "local-test-key",
            });
        }

        services.AddSingleton(functionAppStore);

        services.ConfigureDockerAzureCosmosEmulator();

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