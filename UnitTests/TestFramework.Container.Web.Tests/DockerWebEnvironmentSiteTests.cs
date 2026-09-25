using System;
using System.Collections.Generic;
using System.Linq;
using TestFramework.Container.Sources;
using TestFramework.Container.Web.Sites;
using TestFramework.Core.Environment;
using TestFramework.Core.Exceptions;
using TestFramework.Web;
using TestFramework.Web.Identifier;
using TestFramework.Web.Site;
using Xunit;

namespace TestFramework.Container.Web.Tests;

/// <summary>
/// Covers which components a site declaration resolves, and the bindings that are checked before
/// Docker is touched.
/// </summary>
public class DockerWebEnvironmentSiteTests
{

    private sealed class OrdersApiDefinition : DockerApiDefinition
    {
        public override ApiIdentifier Identifier => "orders";

        public override ContainerSource Source => ContainerSource.Directory(AppContext.BaseDirectory);

        protected override void Configure(DockerApiBuilder builder) => builder.WithoutHealthCheck();
    }

    private sealed class ShopSiteDefinition : DockerSiteDefinition
    {
        public override SiteIdentifier Identifier => "shop";

        public override SiteSource Source => SiteSource.Directory(AppContext.BaseDirectory);

        protected override void Configure(DockerSiteBuilder builder)
        {
        }
    }

    private sealed class DuplicateShopSiteDefinition : DockerSiteDefinition
    {
        public override SiteIdentifier Identifier => "shop";

        public override SiteSource Source => SiteSource.Directory(AppContext.BaseDirectory);

        protected override void Configure(DockerSiteBuilder builder) => builder.WithoutSpaFallback();
    }

    private sealed class ProxyingSiteDefinition : DockerSiteDefinition
    {
        public override SiteIdentifier Identifier => "shop";

        public override SiteSource Source => SiteSource.Directory(AppContext.BaseDirectory);

        protected override void Configure(DockerSiteBuilder builder) => builder.ProxyApi<OrdersApiDefinition>("/api");
    }

    private sealed class ExternalProxyingSiteDefinition : DockerSiteDefinition
    {
        public override SiteIdentifier Identifier => "shop";

        public override SiteSource Source => SiteSource.Directory(AppContext.BaseDirectory);

        protected override void Configure(DockerSiteBuilder builder) => builder.ProxyApi("orders", "/api");
    }

    [Fact]
    public void ResolveComponents_StartsTheSiteForADeclaredSite()
    {
        DockerWebEnvironment environment = new DockerWebEnvironment().Include<ShopSiteDefinition>();

        IReadOnlyCollection<EnvComponentIdentifier> resolved = environment.ResolveComponents([], []);

        Assert.Contains(DockerWebEnvironment.SiteComponentId, resolved);
        Assert.DoesNotContain(DockerWebEnvironment.SqlServerComponentId, resolved);
    }

    [Fact]
    public void ResolveComponents_StartsTheSiteForARequirementItDeclares()
    {
        // A browser step no longer requires a "ui.webapp" this environment has to know about: it resolves
        // the site of the same name itself and requires that, which is what reaches this environment.
        DockerWebEnvironment environment = new DockerWebEnvironment().Include<ShopSiteDefinition>();

        IReadOnlyCollection<EnvComponentIdentifier> resolved = environment.ResolveComponents([], [new EnvironmentRequirement(WebEnvironmentResourceKinds.Site, "shop")]);

        Assert.Contains(DockerWebEnvironment.SiteComponentId, resolved);
        Assert.Contains("web.site/shop", environment.Nodes.Select(node => node.ToString()));
    }

    [Fact]
    public void ResolveComponents_FailsWhenASiteProxiesAnUndeclaredApiByType()
    {
        DockerWebEnvironment environment = new DockerWebEnvironment().Include<ProxyingSiteDefinition>();

        FrameworkConfigurationException exception = Assert.Throws<FrameworkConfigurationException>(() => environment.ResolveComponents([], []));

        Assert.Contains("'orders'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ProxyingSiteDefinition", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveComponents_LetsAnIdentifierTargetResolveFromConfigurationLater()
    {
        // Named by identifier, the API may live outside this environment; the address is resolved
        // against the configuration store when the environment starts, not here.
        DockerWebEnvironment environment = new DockerWebEnvironment().Include<ExternalProxyingSiteDefinition>();

        IReadOnlyCollection<EnvComponentIdentifier> resolved = environment.ResolveComponents([], []);

        Assert.Contains(DockerWebEnvironment.SiteComponentId, resolved);
    }

    [Fact]
    public void Include_FailsWhenTwoDefinitionsClaimTheSameSiteIdentifier()
    {
        DockerWebEnvironment environment = new DockerWebEnvironment().Include<ShopSiteDefinition>();

        FrameworkConfigurationException exception = Assert.Throws<FrameworkConfigurationException>(() => environment.Include<DuplicateShopSiteDefinition>());

        Assert.Contains("'shop'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSiteDefinitions_OrdersByIdentifierSoStartupIsPredictable()
    {
        DockerWebEnvironment environment = new DockerWebEnvironment().Include<ShopSiteDefinition>();

        Assert.Equal(["shop"], [.. environment.GetSiteDefinitions().Select(definition => definition.Identifier.Identifier)]);
    }
}
