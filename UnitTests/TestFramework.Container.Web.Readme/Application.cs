using System.Net;
using System.Threading.Tasks;
using TestFramework.Config;
using TestFramework.Container.Sources;
using TestFramework.Core.Timelines;
using TestFramework.Core.Timelines.Assertions;
using TestFramework.Core.Variables;
using TestFramework.Web;
using TestFramework.Web.Extensions;
using TestFramework.Web.Identifier;

namespace TestFramework.Container.Web.Readme.Application;

#region snippet:api-definition
internal sealed class OrdersApiDefinition : DockerApiDefinition
{
    public override ApiIdentifier Identifier => "orders";

    // Named, not discovered. A relative path resolves against this source file.
    public override ContainerSource Source =>
        ContainerSource.Project("../Orders.Api/Orders.Api.csproj").WithTargetFramework("net10.0");

    protected override void Configure(DockerApiBuilder builder) => builder
        .WithEnvironmentName("Testing")
        .WithHealthPath("/health")
        .UseSql<SalesSqlDefinition>("ConnectionStrings:Sales")
        .WithSetting("Features:UseFakeClock", "true");
}
#endregion

internal static class ApplicationRun
{
    public static async Task<RunningApi> Run(ConfigInstance config)
    {
        #region snippet:api-run
        Timeline timeline = Timeline.Create()
            .Trigger(WebExt.Api.Http("orders").Post("api/orders")
                .WithJsonBody(Var.Const(new CreateOrder("container-order", 7))).Call()).Label("create")
            .FindArtifact("created", WebExt.ArtifactFinder.Sql.Where<Order>("sales", "Name = @name")
                .WithParameter("name", "container-order"))
            .Build();

        TimelineRun run = await timeline.SetupRun(config)
            .SetEnv(DockerWebEnvironment.For<SalesSqlDefinition>().Include<OrdersApiDefinition>())
            .RunAsync();

        run.EnsureRanToCompletion();
        run.ApiStatus("create").Should().Be(HttpStatusCode.Created);
        run.SqlRow<Order>("created").Select(order => order.Quantity).Should().Be(7);
        #endregion

        #region snippet:api-state
        ApiComponentState state = run.EnvironmentContext.GetState<ApiComponentState>(DockerWebEnvironment.ApiComponentId)!;
        RunningApi api = state.GetRequiredApi("orders");
        // api.SettingsJson, api.BaseUrl, api.Plan.ProjectPath, api.Plan.Image, api.Plan.BuiltAtUtc
        #endregion
        return api;
    }

    public static ContainerSource[] Sources(string path)
    {
        #region snippet:container-sources
        ContainerSource sdkBuilt = ContainerSource.Project(path);                          // SDK builds the image        ~8 s
        ContainerSource hostBuilt = ContainerSource.Project(path).BuiltOnHost();           // publish to temp, copy in    ~2 s
        ContainerSource containerBuilt = ContainerSource.Project(path).BuiltInContainer(); // built in Docker, clean host ~6 s
        #endregion

        #region snippet:other-sources
        ContainerSource prebuilt = ContainerSource.Image("orders-api:ci-1234");      // a pipeline already built it
        ContainerSource folder = ContainerSource.Directory(@"C:\out\orders-api");    // this exact folder
        #endregion

        return [sdkBuilt, hostBuilt, containerBuilt, prebuilt, folder];
    }
}
