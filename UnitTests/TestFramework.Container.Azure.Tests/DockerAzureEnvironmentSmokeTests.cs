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
using TestFramework.Azure.ServiceBus;
using TestFramework.Azure.StorageAccount.Blob;
using TestFramework.Azure.StorageAccount.Table;
using TestFramework.Container.Azure;
using TestFramework.Container.Azure.FunctionApp;
using TestFramework.Container.Azure.ServiceBusFunctionApp;
using TestFramework.Config.Builder.InstanceBuilder;
using TestFramework.Core.Environment;
using TestFramework.Core.Exceptions;
using TestFramework.Core.Steps;
using TestFramework.Core.Steps.Options;
using TestFramework.Core.Timelines;
using TestFramework.Core.Timelines.Builder.TimelineBuilder;
using TestFramework.Core.Timelines.Builder.TimelineRunBuilder;
using TestFramework.Core.Variables;
using System.Net;

namespace TestFramework.Container.Azure.Tests;

// README sync note: the README golden sample is backed by the smoke test below.
// If you update that sample path or assertions, update the corresponding README sample as well.
[Collection(DockerAzureSmokeCollectionDefinition.CollectionName)]
public class DockerAzureEnvironmentSmokeTests
{
    internal const string SmokeTableName = "smoketable";

    private readonly DockerAzureSmokeCollectionFixture _fixture;

    public DockerAzureEnvironmentSmokeTests(DockerAzureSmokeCollectionFixture fixture)
    {
        _fixture = fixture;
    }

    internal sealed class ReadmeCosmosDefinition : DockerCosmosDefinition<SmokeTableEntity>
    {
        public override CosmosContainerIdentifier Identifier => "cosmos";
    }

    internal sealed class SmokeStorageDefinition : DockerStorageDefinition
    {
        public override StorageAccountIdentifier Identifier => "storage";
    }

    internal sealed class SmokeCosmosDefinition : DockerCosmosDefinition<SmokeTableEntity>
    {
        public override CosmosContainerIdentifier Identifier => "cosmos";
    }

    internal sealed class SmokeServiceBusDefinition : DockerServiceBusDefinition
    {
        public override ServiceBusIdentifier Identifier => "bus";
    }

    internal sealed class SmokeFunctionTriggerBusDefinition : DockerServiceBusDefinition
    {
        public override ServiceBusIdentifier Identifier => "func-trigger-bus";

        protected override void ConfigureServiceBusTopology(DockerServiceBusTopologyBuilder builder)
            => ConfigureDedicatedFunctionAppServiceBusTopology(builder);
    }

    internal sealed class SmokeFunctionReplyBusDefinition : DockerServiceBusDefinition
    {
        public override ServiceBusIdentifier Identifier => "func-reply-bus";

        protected override void ConfigureServiceBusTopology(DockerServiceBusTopologyBuilder builder)
            => ConfigureDedicatedFunctionAppServiceBusTopology(builder);
    }

    internal sealed class SmokeFunctionAppDefinition : DockerFunctionAppDefinition<LocalFunctionAppSmokeFunction>
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

    internal sealed class SmokeServiceBusFunctionAppDefinition : DockerFunctionAppDefinition<LocalServiceBusFunctionAppSmokeFunction>
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
            .AddQueue("default-queue")
            .AddTopic("smoke-trigger-topic", topic => topic.AddSubscription("smoke-trigger-subscription"))
            .AddTopic("smoke-reply-topic", topic => topic.AddSubscription("smoke-reply-default")));
    }

    [Fact]
    [Trait("Category", "DockerSmoke")]
    public async Task PackageReadme_GoldenSample_RunsAgainstContainerBackedCosmos_WhenSmokeEnabled()
    {
        Timeline timeline = Timeline.Create()
            .Trigger(AzureExt.Trigger.IsLive.Cosmos("cosmos", AlivenessLevel.Authenticated)).WithTimeOut(TimeSpan.FromMinutes(2))
            .Build();

        TimelineRun run = await timeline.SetupRun().SetEnv(_fixture.GetEnv()).RunAsync();

        run.EnsureRanToCompletion();
        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.CosmosDbComponentId));
    }

    [Fact]
    [Trait("Category", "DockerSmoke")]
    public async Task Timeline_CanRunBlobIsLiveWithDockerAzureEnvironment_WhenSmokeEnabled()
    {
        TimelineRun run = await RunSmokeTimelineAsync(
            [builder => builder.SetupArtifact("blob").WithRetry(5, CalcDelays.Fixed(TimeSpan.FromSeconds(2))).Trigger(AzureExt.Trigger.IsLive.Blob("storage", AlivenessLevel.Resource))],
            runBuilder => runBuilder.AddArtifact("blob", new StorageAccountBlobArtifactReference("storage", Var.Const("smoke-blob.txt")), new StorageAccountBlobArtifactData([1, 2, 3], new Dictionary<string, string>())));

        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.AzuriteComponentId));
    }

    [Fact]
    [Trait("Category", "DockerSmoke")]
    public async Task Timeline_CanRunTableIsLiveWithDockerAzureEnvironment_WhenSmokeEnabled()
    {
        TimelineRun run = await RunSmokeTimelineAsync(
            [builder => builder.SetupArtifact("table").Trigger(AzureExt.Trigger.IsLive.Table("storage", AlivenessLevel.Resource)).WithTimeOut(TimeSpan.FromMinutes(1))],
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
        TimelineRun run = await RunSmokeTimelineAsync(
            [builder => builder.Trigger(AzureExt.Trigger.IsLive.Cosmos("cosmos", AlivenessLevel.Authenticated)).WithTimeOut(TimeSpan.FromMinutes(2))]);

        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.CosmosDbComponentId));
    }

    [Fact]
    [Trait("Category", "DockerSmoke")]
    public async Task Timeline_CanRunSqlIsLiveWithDockerAzureEnvironment_WhenSmokeEnabled()
    {
        TimelineRun run = await RunSmokeTimelineAsync(
            [builder => builder.Trigger(AzureExt.Trigger.IsLive.Sql("sql"))]);

        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.MsSqlComponentId));
    }

    [Fact]
    [Trait("Category", "DockerSmoke")]
    public async Task Timeline_CanRunServiceBusStepsWithDockerAzureEnvironment_WhenSmokeEnabled()
    {
        TimelineRun run = await RunSmokeTimelineAsync(
            [builder => builder.Trigger(AzureExt.Trigger.ServiceBus.Send("bus", Var.Const(new ServiceBusMessage("payload"))))]);

        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.ServiceBusComponentId));
        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.MsSqlComponentId));
    }

    [Fact]
    [Trait("Category", "DockerSmoke")]
    public async Task Timeline_CanRunAllAzureStepsAcrossDockerAzureComponents_WhenSmokeEnabled()
    {
        TimelineRun run = await RunSmokeTimelineAsync(
            [
                builder => builder.SetupArtifact("blob").WithRetry(5, CalcDelays.Fixed(TimeSpan.FromSeconds(2))).Trigger(AzureExt.Trigger.IsLive.Blob("storage", AlivenessLevel.Resource)),
                builder => builder.SetupArtifact("table").Trigger(AzureExt.Trigger.IsLive.Table("storage", AlivenessLevel.Resource)).WithTimeOut(TimeSpan.FromMinutes(1)),
                builder => builder.Trigger(AzureExt.Trigger.IsLive.Cosmos("cosmos", AlivenessLevel.Authenticated)).WithTimeOut(TimeSpan.FromMinutes(2)),
                builder => builder.Trigger(AzureExt.Trigger.IsLive.Sql("sql")),
                builder => builder.Trigger(AzureExt.Trigger.ServiceBus.Send("bus", Var.Const(new ServiceBusMessage("payload"))))
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
    public async Task Timeline_CanReachDockerHostedFunctionAppHost_WithoutExternalDependencies()
    {
        Timeline timeline = Timeline.Create()
            .Trigger(AzureExt.Trigger.IsLive.FunctionApp("func", AlivenessLevel.Reachable))
            .WithTimeOut(TimeSpan.FromMinutes(1))
            .Name("function-live")
            .Build();

        TimelineRun run = await timeline.SetupRun().SetEnv(_fixture.GetEnv()).RunAsync();

        run.EnsureRanToCompletion();
        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.FunctionAppComponentId));
    }

    [Fact]
    [Trait("Category", "DockerSmoke")]
    public async Task Timeline_ReusesPersistentInfrastructureAcrossMultipleRuns()
    {
        TimelineRun firstRun = await RunSmokeTimelineAsync(
            [builder => builder.SetupArtifact("blob").WithRetry(5, CalcDelays.Fixed(TimeSpan.FromSeconds(2))).Trigger(AzureExt.Trigger.IsLive.Blob("storage", AlivenessLevel.Resource))],
            runBuilder => runBuilder.AddArtifact("blob", new StorageAccountBlobArtifactReference("storage", Var.Const("reuse-first.txt")), new StorageAccountBlobArtifactData([1, 2, 3], new Dictionary<string, string>())));

        TimelineRun secondRun = await RunSmokeTimelineAsync(
            [builder => builder.SetupArtifact("blob").WithRetry(5, CalcDelays.Fixed(TimeSpan.FromSeconds(2))).Trigger(AzureExt.Trigger.IsLive.Blob("storage", AlivenessLevel.Resource))],
            runBuilder => runBuilder.AddArtifact("blob", new StorageAccountBlobArtifactReference("storage", Var.Const("reuse-second.txt")), new StorageAccountBlobArtifactData([1, 2, 3], new Dictionary<string, string>())));

        Assert.Same(
            firstRun.EnvironmentContext.GetState<object>(DockerAzureEnvironment.NetworkComponentId),
            secondRun.EnvironmentContext.GetState<object>(DockerAzureEnvironment.NetworkComponentId));
        Assert.Same(
            firstRun.EnvironmentContext.GetState<object>(DockerAzureEnvironment.AzuriteComponentId),
            secondRun.EnvironmentContext.GetState<object>(DockerAzureEnvironment.AzuriteComponentId));
    }

    [Fact]
    [Trait("Category", "DockerSmoke")]
    public async Task Timeline_UsesRunLocalServiceBusConfigOverrides_OnTopOfPersistentSnapshot()
    {
        Timeline timeline = Timeline.Create()
            .Trigger(new InspectServiceBusConfigStep()).Name("inspect-servicebus-config")
            .Build();

        TimelineRun run = await timeline.SetupRun().SetEnv(_fixture.GetEnv(builder =>
        {
            builder.AddService(services =>
            {
                ConfigStore<ServiceBusConfig> serviceBusStore = ConfigStore<ServiceBusConfig>.Create("bus", new ServiceBusConfig
                {
                    ConnectionString = "Endpoint=sb://localhost/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=local",
                    QueueName = "run-override-queue",
                    TopicName = null,
                    SubscriptionName = null,
                    RequiredSession = false,
                });
                services.AddSingleton(serviceBusStore);
            });
        })).RunAsync();

        run.EnsureRanToCompletion();

        Assert.Equal("run-override-queue", Assert.IsType<InspectServiceBusConfigResult>(run.Step("inspect-servicebus-config").LastResult.Result).QueueName);
        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.ServiceBusComponentId));
    }

    [Fact]
    [Trait("Category", "DockerSmoke")]
    public async Task Timeline_CanInvokeDockerHostedFunctionAppServiceBusTrigger_WithDedicatedServiceBusHost()
    {
        const string correlationId = "smoke-servicebus-correlation";

        Timeline timeline = Timeline.Create()
            .Trigger(AzureExt.Trigger.IsLive.FunctionApp("func-sb", AlivenessLevel.Reachable)).WithTimeOut(TimeSpan.FromMinutes(1)).Name("func-sb-reachable")
            .Trigger(AzureExt.Trigger.ServiceBus.Send("func-trigger-bus", Var.Const(new ServiceBusMessage(correlationId) { CorrelationId = correlationId })))
            .WaitForEvent(AzureExt.Event.ServiceBus.MessageReceived("func-reply-bus", correlationId: Var.Const(correlationId), completeMessage: Var.Const(true))).WithTimeOut(TimeSpan.FromMinutes(1)).Name("reply-received")
            .Build();

        TimelineRun run = await timeline.SetupRun().SetEnv(_fixture.GetEnv()).RunAsync();

        run.EnsureRanToCompletion();

        ServiceBusReceivedMessageContext result = Assert.IsType<ServiceBusReceivedMessageContext>(run.Step("reply-received").LastResult.Result);
        Assert.Equal(correlationId, result.Message.CorrelationId);
        Assert.Equal("servicebus-smoke-processed", result.Message.Subject);
        Assert.Equal($"processed:{correlationId}", result.Message.Body.ToString());
        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.FunctionAppComponentId));
        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.ServiceBusComponentId));
    }

    private async Task<TimelineRun> RunSmokeTimelineAsync(
        IReadOnlyList<Func<ITimelineBuilder, ITimelineBuilder>> configureSteps,
        Func<ITimelineRunBuilder, ITimelineRunBuilder>? configureRunBuilder = null,
        Action<IConfigInstanceBuilder>? configureConfig = null)
    {
        ITimelineBuilder builder = Timeline.Create();
        foreach (Func<ITimelineBuilder, ITimelineBuilder> configureStep in configureSteps)
            builder = configureStep(builder);

        Timeline timeline = builder.Build();
        ITimelineRunBuilder runBuilder = timeline.SetupRun().SetEnv(_fixture.GetEnv(configureConfig));
        if (configureRunBuilder is not null)
            runBuilder = configureRunBuilder(runBuilder);

        TimelineRun run = await runBuilder.RunAsync();

        run.EnsureRanToCompletion();

        return run;
    }

    internal sealed class SmokeSqlDbContext(DbContextOptions<SmokeSqlDbContext> options) : DbContext(options);

    internal sealed class SmokeTableEntity : ITableEntity
    {
        public string PartitionKey { get; set; } = "pk";
        public string RowKey { get; set; } = "rk";
        public DateTimeOffset? Timestamp { get; set; }
        public global::Azure.ETag ETag { get; set; }
    }

    private sealed class InspectServiceBusConfigStep : TestFramework.Core.Steps.Step<InspectServiceBusConfigResult>, IHasEnvironmentRequirements
    {
        public override string Name => "inspect-servicebus-config";

        public override string Description => "Reads the active Service Bus config for the current run.";

        public override bool DoesReturn => true;

        public IReadOnlyCollection<EnvironmentRequirement> GetEnvironmentRequirements(VariableStore variableStore)
            => [new(AzureEnvironmentResourceKinds.ServiceBus, "bus")];

        public override Task<InspectServiceBusConfigResult?> Execute(IServiceProvider serviceProvider, VariableStore variableStore, TestFramework.Core.Artifacts.ArtifactStore artifactStore, TestFramework.Core.Logging.ScopedLogger logger, CancellationToken cancellationToken)
        {
            ServiceBusConfig config = ((ConfigStore<ServiceBusConfig>)serviceProvider.GetService(typeof(ConfigStore<ServiceBusConfig>))!).GetConfig("bus");
            return Task.FromResult<InspectServiceBusConfigResult?>(new(config.QueueName ?? throw new InvalidOperationException("Service Bus queue name was not configured.")));
        }

        public override TestFramework.Core.Steps.Step<InspectServiceBusConfigResult> Clone() => new InspectServiceBusConfigStep().WithClonedOptions(this);

        public override void DeclareIO(TestFramework.Core.Steps.Options.StepIOContract contract)
        {
        }

        public override TestFramework.Core.Steps.StepInstance<TestFramework.Core.Steps.Step<InspectServiceBusConfigResult>, InspectServiceBusConfigResult> GetInstance()
            => new(this);
    }

    private sealed record InspectServiceBusConfigResult(string QueueName) : StepResultContext;
}