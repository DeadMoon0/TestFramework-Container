using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using TestFramework.Config;
using TestFramework.Container.Sources;
using TestFramework.Core.Timelines;
using TestFramework.Core.Timelines.Assertions;
using TestFramework.Core.Variables;
using TestFramework.Web;
using TestFramework.Web.Extensions;
using TestFramework.Web.Identifier;
using TestFramework.Web.Stub;
using TestFramework.Web.Stub.Mappings;

namespace TestFramework.Container.Web.Readme.Stubs;

#region snippet:stub-definition
internal sealed class PaymentsStubDefinition : StubDefinition
{
    public override StubIdentifier Identifier => "payments";

    protected override void Configure(StubMappingBuilder builder) => builder
        .OnGet("/api/rates/EUR")
            .RespondJson(HttpStatusCode.OK, new { currency = "EUR", rate = 1.08 })
        .OnPost("/api/charges")
            .WithHeader("Idempotency-Key")
            .Respond(StubResponse.Json(HttpStatusCode.Created, new { id = "{{Random Type=Guid}}" }) with { UseTemplating = true });
}
#endregion

internal sealed class OrdersApiDefinition : DockerApiDefinition
{
    public override ApiIdentifier Identifier => "orders";

    public override ContainerSource Source => ContainerSource.Project("../Orders.Api/Orders.Api.csproj");

    #region snippet:stub-wiring
    protected override void Configure(DockerApiBuilder builder) => builder
        .UseSql<SalesSqlDefinition>("ConnectionStrings:Sales")
        .UseStub<PaymentsStubDefinition>("Services:Payments:BaseUrl");
    #endregion
}

internal static class StubRun
{
    public static async Task<TimelineRun> Run(ConfigInstance config)
    {
        #region snippet:stub-wiring
        DockerWebEnvironment environment = DockerWebEnvironment.For<SalesSqlDefinition>()
            .Include<OrdersApiDefinition>()
            .IncludeStub<PaymentsStubDefinition>();
        #endregion

        #region snippet:stub-assertions
        Timeline timeline = Timeline.Create()
            .Trigger(WebExt.Api.Http("orders").Post("api/orders")
                .WithJsonBody(Var.Const(new CreateOrder("charged-order", 3))).Call())
            .WaitForEvent(WebExt.Stub.Called("payments", HttpMethod.Post, "/api/charges")).Label("charged")
            .Trigger(WebExt.Stub.Calls("payments")).Label("calls")
            .Build();

        TimelineRun run = await timeline.SetupRun(config).SetEnv(environment).RunAsync();

        run.StubCall("charged").Select(call => call.Body).Should().Contain("\"amount\":30");
        run.StubCalls("calls").Should().HaveCount(1);
        run.StubUnmatchedCalls("calls").Should().HaveCount(0);   // nothing was called that was not declared
        #endregion
        return run;
    }
}
