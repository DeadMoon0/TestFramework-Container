using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using TestFramework.Config;
using TestFramework.Container.Web.SampleApi;
using TestFramework.Core.Timelines;
using TestFramework.Core.Timelines.Assertions;
using TestFramework.Core.Variables;
using TestFramework.Web;
using TestFramework.Web.Extensions;
using TestFramework.Web.Identifier;
using TestFramework.Web.Sql;
using TestFramework.Web.Stub;
using TestFramework.Web.Stub.Mappings;
using TestFramework.Web.Trigger.IsLive;
using Xunit;

namespace TestFramework.Container.Web.Tests;

/// <summary>
/// Proves the last half of the lane: the application under test calls a dependency, that dependency
/// is a container the test declared, and the test asserts on what was actually sent outwards.
/// </summary>
/// <remarks>
/// Needs a Docker daemon, so it is excluded from the default run. Run with
/// <c>--filter "Category=DockerSmoke"</c>.
/// </remarks>
[Trait("Category", "DockerSmoke")]
public class StubContainerSmokeTests
{
    internal sealed class Order
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Quantity { get; set; }
    }

    private sealed class SalesSqlDefinition : DockerSqlDefinition
    {
        public override SqlIdentifier Identifier => "sales";

        protected override void Configure(DockerSqlBuilder builder) => builder
            .WithDatabase("SalesDb")
            .WithSchemaFromModels<Order>()
            .WithResetMode(SqlResetMode.RecreateDatabase);
    }

    private sealed class PaymentsStubDefinition : StubDefinition
    {
        public override StubIdentifier Identifier => "payments";

        protected override void Configure(StubMappingBuilder builder) => builder
            .OnPost("/api/charges")
                .RespondJson(HttpStatusCode.Created, new { status = "captured" });
    }

    private sealed class OrdersApiDefinition : DockerApiDefinition<SampleApiMarker>
    {
        public override ApiIdentifier Identifier => "orders";

        protected override void Configure(DockerApiBuilder builder) => builder
            .WithHealthPath("/health")
            .UseSql<SalesSqlDefinition>("ConnectionStrings:Sales")
            .UseStub<PaymentsStubDefinition>("Services:Payments:BaseUrl");
    }

    private static ConfigInstance CreateConfig()
        => ConfigInstance.Create()
            .LoadWebConfig()
            .AddWebSqlModels(models => models.For<Order>()
                .Table("Orders")
                .Key(x => x.Id)
                .Identity(x => x.Id)
                .MaxLength(x => x.Name, 200))
            .Build();

    private static DockerWebEnvironment CreateEnvironment()
        => DockerWebEnvironment.For<SalesSqlDefinition>()
            .Include<OrdersApiDefinition>()
            .IncludeStub<PaymentsStubDefinition>();

    [Fact]
    public async Task ApplicationCallsTheStub_AndTheTestSeesWhatItSent()
    {
        Timeline timeline = Timeline.Create()
            .Trigger(WebExt.Api.Http("orders")
                .Post("api/orders")
                .WithJsonBody(Var.Const(new CreateSampleOrder("stubbed-order", 3)))
                .Call()).Name("create")
            .WaitForEvent(WebExt.Stub.Called("payments", HttpMethod.Post, "/api/charges"))
                .WithTimeOut(TimeSpan.FromSeconds(30)).Name("charged")
            .Trigger(WebExt.Stub.Calls("payments")).Name("calls")
            .Build();

        TimelineRun run = await timeline.SetupRun(CreateConfig())
            .SetEnv(CreateEnvironment())
            .RunAsync();

        run.EnsureRanToCompletion();

        // The application answered with what the stub told it, which proves the call really went out
        // and came back rather than being skipped.
        run.ApiStatus("create").Should().Be(HttpStatusCode.Created);
        run.ApiBody("create").Should().Contain("captured");

        // And the stub's own log proves what was sent, which no response body could show.
        run.StubCall("charged").Select(call => call.Body).Should().Contain("\"amount\":30");
        run.StubCalls("calls").Should().HaveCount(1);
        run.StubUnmatchedCalls("calls").Should().HaveCount(0);

        Assert.True(run.EnvironmentContext.Contains(DockerWebEnvironment.StubComponentId));
    }

    [Fact]
    public async Task StubIsReachableFromTheTestProcess_AndReportsWhatItLoaded()
    {
        Timeline timeline = Timeline.Create()
            .Trigger(WebExt.Api.IsLive("orders", ApiAlivenessLevel.Healthy)).Name("live")
            .Build();

        TimelineRun run = await timeline.SetupRun(CreateConfig())
            .SetEnv(CreateEnvironment())
            .RunAsync();

        run.EnsureRanToCompletion();

        StubComponentState? state = run.EnvironmentContext.GetState<StubComponentState>(DockerWebEnvironment.StubComponentId);
        Assert.NotNull(state);
        RunningStub stub = state!.GetRequiredStub("payments");

        // A mapping the server rejected would simply be absent, so the count is the proof it loaded.
        Assert.Equal(1, stub.MappingCount);
        Assert.Equal("stub-payments", stub.NetworkBaseUrl.Host);
        Assert.NotEqual(stub.NetworkBaseUrl, stub.HostBaseUrl);

        ApiComponentState? apiState = run.EnvironmentContext.GetState<ApiComponentState>(DockerWebEnvironment.ApiComponentId);
        Assert.NotNull(apiState);

        // The application was given the network address of the stub, never the host-mapped one.
        // Port 80 is the scheme default, so it does not appear in the address.
        Assert.Contains("http://stub-payments/", apiState!.GetRequiredApi("orders").SettingsJson, StringComparison.Ordinal);
        Assert.DoesNotContain(stub.HostBaseUrl.ToString(), apiState.GetRequiredApi("orders").SettingsJson, StringComparison.Ordinal);
    }
}
