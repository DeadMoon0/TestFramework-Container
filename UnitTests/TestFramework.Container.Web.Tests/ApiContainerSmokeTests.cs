using System;
using System.Net;
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
using TestFramework.Web.Trigger.IsLive;
using Xunit;

namespace TestFramework.Container.Web.Tests;

/// <summary>
/// Proves the whole lane against real containers: an application shipped into an ASP.NET runtime
/// image, talking to a database created from the models the test declares, exercised through its own
/// HTTP surface.
/// </summary>
/// <remarks>
/// Needs a Docker daemon, so it is excluded from the default run. Run with
/// <c>--filter "Category=DockerSmoke"</c>.
/// </remarks>
[Trait("Category", "DockerSmoke")]
public class ApiContainerSmokeTests
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

    private sealed class OrdersApiDefinition : DockerApiDefinition<SampleApiMarker>
    {
        public override ApiIdentifier Identifier => "orders";

        protected override void Configure(DockerApiBuilder builder) => builder
            .WithHealthPath("/health")
            .UseSql<SalesSqlDefinition>("ConnectionStrings:Sales");
    }

    private sealed class HealthOnlyApiDefinition : DockerApiDefinition<SampleApiMarker>
    {
        public override ApiIdentifier Identifier => "health-only";

        protected override void Configure(DockerApiBuilder builder) => builder.WithHealthPath("/health");
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

    [Fact]
    public async Task ApiWritesToTheContainerDatabase_AndTheTestSeesTheRow()
    {
        Timeline timeline = Timeline.Create()
            .Trigger(WebExt.Api.IsLive("orders", ApiAlivenessLevel.Healthy)).Name("live")
            .Trigger(WebExt.Api.Http("orders")
                .Post("api/orders")
                .WithJsonBody(Var.Const(new CreateSampleOrder("container-order", 7)))
                .Call()).Name("create")
            .FindArtifact("created", WebExt.ArtifactFinder.Sql.Where<Order>("sales", "Name = @name")
                .WithParameter("name", Var.Const("container-order")))
            .Trigger(WebExt.Api.Http("orders").Get("api/orders").Call()).Name("list")
            .Build();

        TimelineRun run = await timeline.SetupRun(CreateConfig())
            .SetEnv(DockerWebEnvironment.For<SalesSqlDefinition>().Include<OrdersApiDefinition>())
            .RunAsync();

        run.EnsureRanToCompletion();

        // The application really wrote to the container database, and the test reads it back through
        // its own SQL connection rather than trusting the response.
        run.ApiStatus("create").Should().Be(HttpStatusCode.Created);
        run.SqlRow<Order>("created").Select(order => order.Quantity).Should().Be(7);
        run.ApiStatus("list").Should().Be(HttpStatusCode.OK);
        run.ApiBody("list").Should().Contain("container-order");

        Assert.True(run.EnvironmentContext.Contains(DockerWebEnvironment.ApiComponentId));
        Assert.True(run.EnvironmentContext.Contains(DockerWebEnvironment.SqlServerComponentId));
    }

    [Fact]
    public async Task ApiWithNoDatabase_StartsWithoutASqlServerContainer()
    {
        Timeline timeline = Timeline.Create()
            .Trigger(WebExt.Api.IsLive("health-only", ApiAlivenessLevel.Healthy)).Name("live")
            .Build();

        TimelineRun run = await timeline.SetupRun(CreateConfig())
            .SetEnv(DockerWebEnvironment.For<HealthOnlyApiDefinition>())
            .RunAsync();

        run.EnsureRanToCompletion();
        run.ApiProbe("live").Select(probe => probe.Success).Should().Be(true);

        // The SQL component is a dependency of the application component, so it runs, but with
        // nothing declared it must not start a container.
        Assert.Null(run.EnvironmentContext.GetState<SqlServerComponentState>(DockerWebEnvironment.SqlServerComponentId));
    }

    [Fact]
    public async Task ShippedApi_ReportsWhatWasActuallyPutInTheContainer()
    {
        Timeline timeline = Timeline.Create()
            .Trigger(WebExt.Api.IsLive("orders", ApiAlivenessLevel.Healthy)).Name("live")
            .Build();

        TimelineRun run = await timeline.SetupRun(CreateConfig())
            .SetEnv(DockerWebEnvironment.For<SalesSqlDefinition>().Include<OrdersApiDefinition>())
            .RunAsync();

        run.EnsureRanToCompletion();

        ApiComponentState? state = run.EnvironmentContext.GetState<ApiComponentState>(DockerWebEnvironment.ApiComponentId);
        Assert.NotNull(state);
        RunningApi api = state!.GetRequiredApi("orders");

        Assert.Equal("appsettings.Testing.json", api.SettingsFileName);
        Assert.Contains("TestFramework.Container.Web.SampleApi", api.ShippedDirectory, StringComparison.Ordinal);

        // The application is given the network address of the database, never the host-mapped one.
        Assert.Contains($"Data Source={DockerWebDefaults.MsSqlNetworkAlias},1433", api.SettingsJson, StringComparison.Ordinal);
    }
}
