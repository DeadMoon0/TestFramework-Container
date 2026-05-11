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
using TestFramework.Azure.LogicApp;
using TestFramework.Azure.LogicApp.Trigger;
using TestFramework.Azure.StorageAccount.Blob;
using TestFramework.Azure.StorageAccount.Table;
using TestFramework.Container.Azure;
using TestFramework.Container.Azure.FunctionApp;
using TestFramework.Container.Azure.ServiceBusFunctionApp;
using TestFramework.Core.Exceptions;
using TestFramework.Core.Steps.Options;
using TestFramework.Core.Timelines;
using TestFramework.Core.Timelines.Builder.TimelineBuilder;
using TestFramework.Core.Timelines.Builder.TimelineRunBuilder;
using TestFramework.Core.Variables;
using System.Net;

namespace TestFramework.Container.Azure.Tests;

// README sync note: the README golden sample is backed by the smoke test below.
// If you update that sample path or assertions, update the corresponding README sample as well.
[Collection(DockerAzureHostedCollectionDefinition.CollectionName)]
public class DockerAzureEnvironmentSmokeTests
{
    internal const string SmokeTableName = "smoketable";
    private static readonly string SmokeLogicAppPath = Path.Combine("TestFramework-Container", "UnitTests", "TestFramework.Container.Azure.LogicApp");

    private readonly DockerAzureHostedCollectionFixture _fixture;

    public DockerAzureEnvironmentSmokeTests(DockerAzureHostedCollectionFixture fixture)
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

    internal sealed class SmokeLogicAppDefinition : DockerLogicAppDefinition
    {
        public override LogicAppIdentifier Identifier => "logic";

        public override string Path => SmokeLogicAppPath;

        protected override LogicAppConfig? CreateDefaultConfig() => new()
        {
            WorkflowName = "SmokeWorkflow",
            Standard = new LogicAppStandardConfig
            {
                BaseUrl = "http://localhost/",
            },
        };
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
        using ServiceProvider serviceProvider = _fixture.CreateServiceProvider();
        Timeline timeline = Timeline.Create()
            .Trigger(AzureTF.Trigger.IsLive.Cosmos("cosmos", AlivenessLevel.Authenticated)).WithTimeOut(TimeSpan.FromMinutes(2))
            .Build();

        TimelineRun run = await timeline
            .SetupRun(serviceProvider)
            .SetEnv(_fixture.CreateEnvironment())
            .RunAsync();

        run.EnsureRanToCompletion();
        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.CosmosDbComponentId));
    }

    [Fact]
    [Trait("Category", "DockerSmoke")]
    public async Task Timeline_CanRunBlobIsLiveWithDockerAzureEnvironment_WhenSmokeEnabled()
    {
        using ServiceProvider serviceProvider = _fixture.CreateServiceProvider();
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
        using ServiceProvider serviceProvider = _fixture.CreateServiceProvider();
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
        using ServiceProvider serviceProvider = _fixture.CreateServiceProvider();
        TimelineRun run = await RunSmokeTimelineAsync(
            serviceProvider,
            [builder => builder.Trigger(AzureTF.Trigger.IsLive.Cosmos("cosmos", AlivenessLevel.Authenticated)).WithTimeOut(TimeSpan.FromMinutes(2))]);

        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.CosmosDbComponentId));
    }

    [Fact]
    [Trait("Category", "DockerSmoke")]
    public async Task Timeline_CanRunSqlIsLiveWithDockerAzureEnvironment_WhenSmokeEnabled()
    {
        using ServiceProvider serviceProvider = _fixture.CreateServiceProvider();
        TimelineRun run = await RunSmokeTimelineAsync(
            serviceProvider,
            [builder => builder.Trigger(AzureTF.Trigger.IsLive.Sql("sql"))]);

        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.MsSqlComponentId));
    }

    [Fact]
    [Trait("Category", "DockerSmoke")]
    public async Task Timeline_CanRunServiceBusStepsWithDockerAzureEnvironment_WhenSmokeEnabled()
    {
        using ServiceProvider serviceProvider = _fixture.CreateServiceProvider();
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
        using ServiceProvider serviceProvider = _fixture.CreateServiceProvider();
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
    public async Task Timeline_CanReachDockerHostedFunctionAppHost_WithoutExternalDependencies()
    {
        using ServiceProvider serviceProvider = _fixture.CreateServiceProvider(withFunctionApp: true);

        Timeline timeline = Timeline.Create()
            .Trigger(AzureTF.Trigger.IsLive.FunctionApp("func", AlivenessLevel.Reachable))
            .WithTimeOut(TimeSpan.FromMinutes(1))
            .Name("function-live")
            .Build();

        TimelineRun run = await timeline
            .SetupRun(serviceProvider)
            .SetEnv(_fixture.CreateEnvironment())
            .RunAsync();

        run.EnsureRanToCompletion();
        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.FunctionAppComponentId));
    }

    [Fact]
    [Trait("Category", "DockerSmoke")]
    public async Task Timeline_CanInvokeDockerHostedLogicApp_AndObserveCompletedRun()
    {
        using ServiceProvider serviceProvider = _fixture.CreateServiceProvider();

        Timeline timeline = Timeline.Create()
            .Trigger(
                AzureTF.Trigger.LogicApp
                    .Http("logic")
                    .Workflow("SmokeWorkflow")
                    .Manual()
                    .WithBody(Var.Const("{\"smoke\":\"payload\"}"))
                    .CallForRunContext())
            .WithTimeOut(TimeSpan.FromMinutes(2))
            .Name("logic-call")
            .CaptureResultAs("logicRun")
            .WaitForEvent(AzureTF.Event.LogicApp.RunCompleted("logic", Var.Ref<LogicAppRunContext>("logicRun")))
            .WithTimeOut(TimeSpan.FromMinutes(2))
            .Name("logic-run-completed")
            .Build();

        TimelineRun run = await timeline
            .SetupRun(serviceProvider)
            .SetEnv(_fixture.CreateEnvironment())
            .RunAsync();

        run.EnsureRanToCompletion();

        LogicAppRunContext result = Assert.IsType<LogicAppRunContext>(run.Step("logic-call").LastResult.Result);
        Assert.Equal("SmokeWorkflow", result.WorkflowName);
        Assert.False(string.IsNullOrWhiteSpace(result.RunId));

        LogicAppRunDetails completed = Assert.IsType<LogicAppRunDetails>(run.Step("logic-run-completed").LastResult.Result);
        Assert.Equal(result.RunId, completed.RunId);
        Assert.Equal(LogicAppRunStatus.Succeeded, completed.Status);
        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.LogicAppComponentId));
    }

    [Fact]
    [Trait("Category", "DockerSmoke")]
    public async Task Timeline_CanInvokeDockerHostedStatelessLogicApp_AndCaptureResult()
    {
        using ServiceProvider serviceProvider = _fixture.CreateServiceProvider();

        Timeline timeline = Timeline.Create()
            .Trigger(
                AzureTF.Trigger.LogicApp
                    .Http("logic")
                    .Workflow("SmokeStatelessWorkflow")
                    .Manual()
                    .WithBody(Var.Const("{\"smoke\":\"payload\"}"))
                    .CallAndCapture())
            .WithTimeOut(TimeSpan.FromMinutes(2))
            .Name("logic-stateless-call")
            .Build();

        TimelineRun run = await timeline
            .SetupRun(serviceProvider)
            .SetEnv(_fixture.CreateEnvironment())
            .RunAsync();

        run.EnsureRanToCompletion();

        LogicAppCapturedResult result = Assert.IsType<LogicAppCapturedResult>(run.Step("logic-stateless-call").LastResult.Result);
        Assert.Equal("SmokeStatelessWorkflow", result.WorkflowName);
        Assert.Equal("manual", result.TriggerName);
        Assert.Equal(HttpStatusCode.Accepted, result.StatusCode);
        Assert.Equal(LogicAppRunStatus.Succeeded, result.Status);
        Assert.Contains("logic-smoke-stateless-processed", result.ResponseBody, StringComparison.Ordinal);
        Assert.Contains("payload", result.ResponseBody, StringComparison.Ordinal);
        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.LogicAppComponentId));
    }

    [Fact]
    [Trait("Category", "DockerSmoke")]
    public async Task Timeline_CanInvokeDockerHostedTimerLogicApp_AndObserveCompletedRun()
    {
        using ServiceProvider serviceProvider = _fixture.CreateServiceProvider();

        Timeline timeline = Timeline.Create()
            .Trigger(
                AzureTF.Trigger.LogicApp
                    .Http("logic")
                    .Workflow("SmokeTimerWorkflow")
                    .Timer()
                    .CallForRunContext())
            .WithTimeOut(TimeSpan.FromMinutes(2))
            .Name("logic-timer-call")
            .CaptureResultAs("logicTimerRun")
            .WaitForEvent(AzureTF.Event.LogicApp.RunCompleted("logic", Var.Ref<LogicAppRunContext>("logicTimerRun")))
            .WithTimeOut(TimeSpan.FromMinutes(2))
            .Name("logic-timer-run-completed")
            .Build();

        TimelineRun run = await timeline
            .SetupRun(serviceProvider)
            .SetEnv(_fixture.CreateEnvironment())
            .RunAsync();

        run.EnsureRanToCompletion();

        LogicAppRunContext result = Assert.IsType<LogicAppRunContext>(run.Step("logic-timer-call").LastResult.Result);
        Assert.Equal("SmokeTimerWorkflow", result.WorkflowName);
        Assert.False(string.IsNullOrWhiteSpace(result.RunId));

        LogicAppRunDetails completed = Assert.IsType<LogicAppRunDetails>(run.Step("logic-timer-run-completed").LastResult.Result);
        Assert.Equal(result.RunId, completed.RunId);
        Assert.Equal(LogicAppRunStatus.Succeeded, completed.Status);
        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.LogicAppComponentId));
    }

    [Fact]
    [Trait("Category", "DockerSmoke")]
    public async Task Timeline_CanInvokeDockerHostedFunctionAppServiceBusTrigger_WithDedicatedServiceBusHost()
    {
        const string correlationId = "smoke-servicebus-correlation";

        using ServiceProvider serviceProvider = _fixture.CreateServiceProvider();

        Timeline timeline = Timeline.Create()
            .Trigger(AzureTF.Trigger.IsLive.FunctionApp("func-sb", AlivenessLevel.Reachable)).WithTimeOut(TimeSpan.FromMinutes(1)).Name("func-sb-reachable")
            .Trigger(AzureTF.Trigger.ServiceBus.Send("func-trigger-bus", Var.Const(new ServiceBusMessage(correlationId) { CorrelationId = correlationId })))
            .WaitForEvent(AzureTF.Event.ServiceBus.MessageReceived("func-reply-bus", correlationId: Var.Const(correlationId), completeMessage: Var.Const(true))).WithTimeOut(TimeSpan.FromMinutes(1)).Name("reply-received")
            .Build();

        TimelineRun run = await timeline
            .SetupRun(serviceProvider)
            .SetEnv(_fixture.CreateEnvironment())
            .RunAsync();

        run.EnsureRanToCompletion();

        ServiceBusReceivedMessage message = Assert.IsType<ServiceBusReceivedMessage>(run.Step("reply-received").LastResult.Result);
        Assert.Equal(correlationId, message.CorrelationId);
        Assert.Equal("servicebus-smoke-processed", message.Subject);
        Assert.Equal($"processed:{correlationId}", message.Body.ToString());
        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.FunctionAppComponentId));
        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.ServiceBusComponentId));
    }

    private async Task<TimelineRun> RunSmokeTimelineAsync(
        IServiceProvider serviceProvider,
        IReadOnlyList<Func<ITimelineBuilder, ITimelineBuilderModifier>> configureSteps,
        Func<ITimelineRunBuilder, ITimelineRunBuilder>? configureRunBuilder = null)
    {
        ITimelineBuilder builder = Timeline.Create();
        foreach (Func<ITimelineBuilder, ITimelineBuilderModifier> configureStep in configureSteps)
            builder = configureStep(builder);

        Timeline timeline = builder.Build();
        ITimelineRunBuilder runBuilder = timeline.SetupRun(serviceProvider);
        if (configureRunBuilder is not null)
            runBuilder = configureRunBuilder(runBuilder);

        TimelineRun run = await runBuilder
            .SetEnv(_fixture.CreateEnvironment())
            .RunAsync();

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
}