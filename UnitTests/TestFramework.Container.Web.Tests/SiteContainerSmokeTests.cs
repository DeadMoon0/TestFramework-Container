using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Config;
using TestFramework.Container.Sources;
using TestFramework.Container.Web.Sites;
using TestFramework.Container.Web.Tests.Shared;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;
using TestFramework.Core.Logging;
using TestFramework.Core.Steps;
using TestFramework.Core.Steps.Options;
using TestFramework.Core.Timelines;
using TestFramework.Core.Variables;
using TestFramework.Core.Environment.Graph;
using TestFramework.Web;
using TestFramework.Web.Configuration;
using TestFramework.Web.Extensions;
using TestFramework.Web.Identifier;
using TestFramework.Web.Site;
using TestFramework.Web.Sql;
using Xunit;

namespace TestFramework.Container.Web.Tests;

/// <summary>
/// Proves the site lane against real containers: a payload served by nginx, deep links falling back
/// to the page, the proxy reaching the application over the environment's network, and the runtime
/// configuration carrying the address a browser can use.
/// </summary>
/// <remarks>
/// Needs a Docker daemon, so it is excluded from the default run. Run with
/// <c>--filter "Category=DockerSmoke"</c>. The fetch steps run inside the timeline, where the
/// containers are alive, and resolve the site's address the way a browser step would: from the
/// published configuration, by identifier.
/// </remarks>
[Trait("Category", "DockerSmoke")]
public class SiteContainerSmokeTests
{
    private sealed class StaticSiteDefinition : DockerSiteDefinition
    {
        public override SiteIdentifier Identifier => "shop";

        public override SiteSource Source => SiteSource.Directory(SiteTestEnvironmentGate.StaticSiteDirectory);

        protected override void Configure(DockerSiteBuilder builder)
        {
        }
    }

    private sealed class NoFallbackSiteDefinition : DockerSiteDefinition
    {
        public override SiteIdentifier Identifier => "shop-plain";

        public override SiteSource Source => SiteSource.Directory(SiteTestEnvironmentGate.StaticSiteDirectory);

        protected override void Configure(DockerSiteBuilder builder) => builder.WithoutSpaFallback();
    }

    private sealed class ProxyingSiteDefinition : DockerSiteDefinition
    {
        public override SiteIdentifier Identifier => "shop";

        public override SiteSource Source => SiteSource.Directory(SiteTestEnvironmentGate.StaticSiteDirectory);

        protected override void Configure(DockerSiteBuilder builder) => builder
            .ProxyApi<OrdersApiDefinition>("/api")
            .ConfigJsonApi<OrdersApiDefinition>("apiBaseUrl");
    }

    private sealed class SalesSqlDefinition : DockerSqlDefinition
    {
        public override SqlIdentifier Identifier => "sales";

        protected override void Configure(DockerSqlBuilder builder) => builder
            .WithDatabase("SalesDb")
            .WithSchemaFromModels<ApiContainerSmokeTests.Order>()
            .WithResetMode(SqlResetMode.RecreateDatabase);
    }

    private sealed class OrdersApiDefinition : DockerApiDefinition
    {
        public override ApiIdentifier Identifier => "orders";

        public override ContainerSource Source =>
            ContainerSource.Project("../TestFramework.Container.Web.SampleApi/TestFramework.Container.Web.SampleApi.csproj")
                .WithTargetFramework(ContainerOutputResolver.ResolveTargetFramework(typeof(SiteContainerSmokeTests).Assembly));

        protected override void Configure(DockerApiBuilder builder) => builder
            .WithHealthPath("/health")
            .UseSql<SalesSqlDefinition>("ConnectionStrings:Sales");
    }

    private static ConfigInstance CreateConfig()
        => ConfigInstance.Create()
            .LoadWebConfig()
            .AddWebSqlModels(models => models.For<ApiContainerSmokeTests.Order>()
                .Table("Orders")
                .Key(x => x.Id)
                .Identity(x => x.Id)
                .MaxLength(x => x.Name, 200))
            .Build();

    [Fact]
    public async Task Site_ServesTheIndex_AndDeepLinksFallBackToIt()
    {
        Timeline timeline = Timeline.Create()
            .Trigger(new FetchSiteStep("shop", "/")).Name("index")
            .Trigger(new FetchSiteStep("shop", "/orders")).Name("deep-link")
            .Trigger(new FetchSiteStep("shop", "/app.js")).Name("asset")
            .Build();

        TimelineRun run = await timeline.SetupRun(CreateConfig())
            .SetEnv(new DockerWebEnvironment().Include<StaticSiteDefinition>())
            .RunAsync();

        run.EnsureRanToCompletion();

        Assert.Contains("Sample Static Site", Fetched(run, "index").Body, StringComparison.Ordinal);

        // The deep link answers with the page itself: the route is the application's, not the server's.
        Assert.Equal(200, Fetched(run, "deep-link").StatusCode);
        Assert.Contains("Sample Static Site", Fetched(run, "deep-link").Body, StringComparison.Ordinal);

        // A real asset is served as itself, not swallowed by the fallback.
        Assert.Contains("history-API routing", Fetched(run, "asset").Body, StringComparison.Ordinal);

        Assert.True(run.EnvironmentContext.Contains(DockerWebEnvironment.SiteComponentId));
    }

    [Fact]
    public async Task SiteWithoutFallback_AnswersUnknownPathsWith404()
    {
        Timeline timeline = Timeline.Create()
            .Trigger(new FetchSiteStep("shop-plain", "/orders")).Name("deep-link")
            .Build();

        TimelineRun run = await timeline.SetupRun(CreateConfig())
            .SetEnv(new DockerWebEnvironment().Include<NoFallbackSiteDefinition>())
            .RunAsync();

        run.EnsureRanToCompletion();

        Assert.Equal(404, Fetched(run, "deep-link").StatusCode);
    }

    [Fact]
    public async Task Proxy_ReachesTheApi_AndConfigJsonCarriesTheHostAddress()
    {
        Timeline timeline = Timeline.Create()
            .Trigger(new FetchSiteStep("shop", "/api/orders")).Name("proxied")
            .Trigger(new FetchSiteStep("shop", "/assets/config.json")).Name("config")
            .Build();

        TimelineRun run = await timeline.SetupRun(CreateConfig())
            .SetEnv(DockerWebEnvironment.For<SalesSqlDefinition>()
                .Include<OrdersApiDefinition>()
                .Include<ProxyingSiteDefinition>())
            .RunAsync();

        run.EnsureRanToCompletion();

        // The request went to the site's own origin and came back with the application's answer:
        // browser traffic on a relative path never needs a route to the API container.
        Assert.Equal(200, Fetched(run, "proxied").StatusCode);
        Assert.StartsWith("[", Fetched(run, "proxied").Body, StringComparison.Ordinal);

        // The generated config carries the host-mapped address a browser could call directly, and
        // keeps the fields the payload shipped.
        FetchSiteResult config = Fetched(run, "config");
        Assert.Contains("http://", config.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("api-orders", config.Body, StringComparison.Ordinal);
        Assert.Contains("\"theme\": \"plain\"", config.Body, StringComparison.Ordinal);

        // What was generated is inspectable after the fact, container or no container.
        SiteComponentState? state = run.EnvironmentContext.GetState<SiteComponentState>(DockerWebEnvironment.SiteComponentId);
        Assert.NotNull(state);
        RunningSite site = state!.GetRequiredSite("shop");
        Assert.NotNull(site.NginxConfig);
        Assert.Contains("proxy_pass http://api-orders:8080;", site.NginxConfig, StringComparison.Ordinal);
        Assert.Equal(SiteSourceKind.Directory, site.Plan.Kind);
    }

    [Fact]
    public async Task AngularDist_ServesAndProxies()
    {
        if (SiteTestEnvironmentGate.AngularSkipReason() is { } reason)
        {
            // The fixture is a plain folder built outside the solution; absent output is a skip, not
            // a failure, exactly like the browser suite's gate.
            Console.WriteLine(reason);
            return;
        }

        Timeline timeline = Timeline.Create()
            .Trigger(new FetchSiteStep("shop", "/orders")).Name("deep-link")
            .Trigger(new FetchSiteStep("shop", "/api/orders")).Name("proxied")
            .Build();

        TimelineRun run = await timeline.SetupRun(CreateConfig())
            .SetEnv(DockerWebEnvironment.For<SalesSqlDefinition>()
                .Include<OrdersApiDefinition>()
                .Include<AngularSiteDefinition>())
            .RunAsync();

        run.EnsureRanToCompletion();

        // The deep link answers with the Angular page, whose router owns the path.
        Assert.Equal(200, Fetched(run, "deep-link").StatusCode);
        Assert.Contains("app-root", Fetched(run, "deep-link").Body, StringComparison.Ordinal);

        Assert.Equal(200, Fetched(run, "proxied").StatusCode);
        Assert.StartsWith("[", Fetched(run, "proxied").Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NpmProject_BuildsOnTheHost_AndServes()
    {
        if (!SiteTestEnvironmentGate.NpmOptedIn)
        {
            Console.WriteLine($"Set {SiteTestEnvironmentGate.NpmOptInVariable}=1 to run the tests that invoke npm themselves.");
            return;
        }

        Timeline timeline = Timeline.Create()
            .Trigger(new FetchSiteStep("shop", "/")).Name("index")
            .Build();

        TimelineRun run = await timeline.SetupRun(CreateConfig())
            .SetEnv(new DockerWebEnvironment().Include<NpmBuiltSiteDefinition>())
            .RunAsync();

        run.EnsureRanToCompletion();

        Assert.Contains("app-root", Fetched(run, "index").Body, StringComparison.Ordinal);

        SiteComponentState? state = run.EnvironmentContext.GetState<SiteComponentState>(DockerWebEnvironment.SiteComponentId);
        Assert.Equal(SiteSourceKind.NpmProject, state!.GetRequiredSite("shop").Plan.Kind);
    }

    private sealed class AngularSiteDefinition : DockerSiteDefinition
    {
        public override SiteIdentifier Identifier => "shop";

        public override SiteSource Source => SiteSource.Directory(SiteTestEnvironmentGate.AngularDistDirectory);

        protected override void Configure(DockerSiteBuilder builder) => builder
            .ProxyApi<OrdersApiDefinition>("/api");
    }

    private sealed class NpmBuiltSiteDefinition : DockerSiteDefinition
    {
        public override SiteIdentifier Identifier => "shop";

        public override SiteSource Source => SiteSource.NpmProject(SiteTestEnvironmentGate.AngularProjectDirectory)
            .WithDistPath("dist/sample-site/browser");

        protected override void Configure(DockerSiteBuilder builder)
        {
        }
    }

    [Fact]
    public async Task NpmProject_BuildsInsideAContainer_AndServes()
    {
        if (!SiteTestEnvironmentGate.NpmOptedIn)
        {
            Console.WriteLine($"Set {SiteTestEnvironmentGate.NpmOptInVariable}=1 to run the tests that invoke npm themselves.");
            return;
        }

        Timeline timeline = Timeline.Create()
            .Trigger(new FetchSiteStep("shop", "/")).Name("index")
            .Build();

        TimelineRun run = await timeline.SetupRun(CreateConfig())
            .SetEnv(new DockerWebEnvironment().Include<ContainerBuiltSiteDefinition>())
            .RunAsync();

        run.EnsureRanToCompletion();

        Assert.Contains("app-root", Fetched(run, "index").Body, StringComparison.Ordinal);

        // The scratch directory the build staged into is the run's litter and travels on the plan,
        // so teardown can remove it.
        SiteComponentState? state = run.EnvironmentContext.GetState<SiteComponentState>(DockerWebEnvironment.SiteComponentId);
        Assert.NotNull(state!.GetRequiredSite("shop").Plan.TemporaryRoot);
    }

    private sealed class ContainerBuiltSiteDefinition : DockerSiteDefinition
    {
        public override SiteIdentifier Identifier => "shop";

        public override SiteSource Source => SiteSource.NpmProject(SiteTestEnvironmentGate.AngularProjectDirectory)
            .BuiltInContainer()
            .WithDistPath("dist/sample-site/browser");

        protected override void Configure(DockerSiteBuilder builder)
        {
        }
    }

    private static FetchSiteResult Fetched(TimelineRun run, string stepName)
        => Assert.IsType<FetchSiteResult>(run.Step(stepName).LastResult.Result);

    /// <summary>
    /// Fetches a path from a site the way a browser step would find it: by identifier, from the run.
    /// Declaring the browser steps' own requirement kind is deliberate -- it proves the environment maps
    /// <c>ui.webapp</c> to the site component.
    /// </summary>
    /// <remarks>
    /// It asks the run rather than a configuration store, because that is where a started container's
    /// address now is. The host viewpoint, because this step runs in the test process; the same identifier
    /// answers a peer container with the network alias instead, which a single-address store could not say.
    /// </remarks>
    private sealed class FetchSiteStep(string siteIdentifier, string path) : Step<FetchSiteResult>, IHasEnvironmentRequirements
    {
        public override string Name => "fetch-site";

        public override string Description => $"Fetches '{path}' from site '{siteIdentifier}'.";

        public override bool DoesReturn => true;

        public IReadOnlyCollection<EnvironmentRequirement> GetEnvironmentRequirements(VariableStore variableStore)
            => [new EnvironmentRequirement("ui.webapp", siteIdentifier)];

        public override async Task<FetchSiteResult?> Execute(RunContext context)
        {
            Uri baseUrl = new(
                context.Values.Require(
                    ValueRef.For(WebEnvironmentResourceKinds.Site, siteIdentifier, ValueNames.BaseUrl),
                    ResourceVantage.Host),
                UriKind.Absolute);

            using HttpClient client = new() { BaseAddress = baseUrl };
            using HttpResponseMessage response = await client.GetAsync(path.TrimStart('/'), context.Deadline.Token).ConfigureAwait(false);

            string body = await response.Content.ReadAsStringAsync(context.Deadline.Token).ConfigureAwait(false);
            return new FetchSiteResult((int)response.StatusCode, body);
        }

        public override Step<FetchSiteResult> Clone() => new FetchSiteStep(siteIdentifier, path).WithClonedOptions(this);

        public override void DeclareIO(StepIOContract contract)
        {
        }

        public override StepInstance<Step<FetchSiteResult>, FetchSiteResult> GetInstance() => new(this);
    }

    private sealed record FetchSiteResult(int StatusCode, string Body) : StepResultContext;
}
