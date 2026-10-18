using TestFramework.Container.Web.Readme.Application;
using TestFramework.Container.Web.Sites;
using TestFramework.Core.Timelines;
using TestFramework.Web.Site;

namespace TestFramework.Container.Web.Readme.Sites;

#region snippet:site-definition
internal sealed class ShopSiteDefinition : DockerSiteDefinition
{
    public override SiteIdentifier Identifier => "shop";   // the same string browser steps use

    public override SiteSource Source =>
        SiteSource.NpmProject("../../Shop.Frontend");       // npm run build, dist probed by convention

    protected override void Configure(DockerSiteBuilder builder) => builder
        .ProxyApi<OrdersApiDefinition>("/api");
}
#endregion

internal sealed class ConfiguredSiteDefinition : DockerSiteDefinition
{
    public override SiteIdentifier Identifier => "shop";

    public override SiteSource Source => SiteSource.Directory("../dist/shop/browser");

    protected override void Configure(DockerSiteBuilder builder)
    {
        #region snippet:config-file
        builder.WithConfigFile("env.js", ctx =>
            $"window.__env = {{ ordersApi: \"{ctx.Addresses.ApiBaseUrl("orders")}\" }};");

        // or patch one field of the shipped JSON without rebuilding the rest:
        builder.WithConfigFile("assets/config.json", ctx =>
            ctx.MergeJson(("apiBaseUrl", ctx.Addresses.ApiBaseUrl("orders").ToString())));
        #endregion
    }
}

internal static class SiteRun
{
    public static DockerWebEnvironment Environment()
    {
        #region snippet:site-definition
        DockerWebEnvironment environment = DockerWebEnvironment.For<SalesSqlDefinition>()
            .Include<OrdersApiDefinition>()
            .Include<ShopSiteDefinition>();
        #endregion
        return environment;
    }

    public static RunningSite State(TimelineRun run)
    {
        #region snippet:site-state
        SiteComponentState state = run.EnvironmentContext.GetState<SiteComponentState>(DockerWebEnvironment.SiteComponentId)!;
        RunningSite site = state.GetRequiredSite("shop");
        // site.BaseUrl, site.NginxConfig, site.GeneratedFiles["assets/config.json"], site.Plan
        #endregion
        return site;
    }
}
