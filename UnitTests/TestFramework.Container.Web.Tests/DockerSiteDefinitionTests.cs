using System;
using System.Linq;
using TestFramework.Container.Sources;
using TestFramework.Container.Web.Components;
using TestFramework.Container.Web.Sites;
using TestFramework.Core.Exceptions;
using TestFramework.Web.Identifier;
using TestFramework.Web.Site;
using TestFramework.Web.Stub;
using TestFramework.Web.Stub.Mappings;
using Xunit;

namespace TestFramework.Container.Web.Tests;

/// <summary>
/// Covers the site declaration surface: what the builder collects, and the inconsistencies it
/// refuses before Docker is touched.
/// </summary>
public class DockerSiteDefinitionTests
{
    private sealed class OrdersApiDefinition : DockerApiDefinition
    {
        public override ApiIdentifier Identifier => "orders";

        public override ContainerSource Source => ContainerSource.Directory(AppContext.BaseDirectory);

        protected override void Configure(DockerApiBuilder builder) => builder.WithoutHealthCheck();
    }

    private sealed class PricingStubDefinition : StubDefinition
    {
        public override StubIdentifier Identifier => "pricing";

        protected override void Configure(StubMappingBuilder builder)
        {
        }
    }

    private sealed class CompleteSiteDefinition : DockerSiteDefinition
    {
        public override SiteIdentifier Identifier => "shop";

        public override SiteSource Source => SiteSource.Directory(AppContext.BaseDirectory);

        protected override void Configure(DockerSiteBuilder builder) => builder
            .WithPort(8080)
            .WithReadinessPath("status")
            .WithReadinessTimeout(TimeSpan.FromSeconds(5))
            .ProxyApi<OrdersApiDefinition>("/api")
            .ProxyStub<PricingStubDefinition>("/pricing/", stripPrefix: true)
            .ConfigJsonApi<OrdersApiDefinition>("apiBaseUrl")
            .WithConfigJsonValue("featureFlag", "on");
    }

    private sealed class DefaultsSiteDefinition : DockerSiteDefinition
    {
        public override SiteIdentifier Identifier => "shop";

        public override SiteSource Source => SiteSource.Directory(AppContext.BaseDirectory);

        protected override void Configure(DockerSiteBuilder builder)
        {
        }
    }

    [Fact]
    public void Build_CarriesTheRoutesBindingsAndServing()
    {
        DockerSiteSpec spec = new CompleteSiteDefinition().Build();

        Assert.Equal(8080, spec.InternalPort);
        Assert.Equal("/status", spec.ReadinessPath);
        Assert.Equal(TimeSpan.FromSeconds(5), spec.ReadinessTimeout);
        Assert.True(spec.SpaFallback);

        DockerSiteProxyRoute api = Assert.Single(spec.ProxyRoutes, route => route.TargetKind == SiteTargetKind.Api);
        Assert.Equal("orders", api.TargetIdentifier);
        Assert.False(api.StripPrefix);
        Assert.True(api.DeclaredByType);

        DockerSiteProxyRoute stub = Assert.Single(spec.ProxyRoutes, route => route.TargetKind == SiteTargetKind.Stub);
        Assert.Equal("pricing", stub.TargetIdentifier);
        Assert.True(stub.StripPrefix);

        Assert.True(spec.WritesConfigJson);
        Assert.Equal("apiBaseUrl", Assert.Single(spec.ConfigJsonBindings).JsonPath);
        Assert.Equal("on", spec.ConfigJsonValues["featureFlag"]);
    }

    [Fact]
    public void Build_AppliesTheDefaults()
    {
        DockerSiteSpec spec = new DefaultsSiteDefinition().Build();

        Assert.Null(spec.Image);
        Assert.Equal(DockerWebDefaults.SiteInternalPort, spec.InternalPort);
        Assert.Equal("/", spec.ReadinessPath);
        Assert.Equal(DockerWebDefaults.SiteReadinessTimeout, spec.ReadinessTimeout);
        Assert.True(spec.SpaFallback);
        Assert.Equal(DockerWebDefaults.SiteConfigJsonPath, spec.ConfigJsonPath);
        Assert.False(spec.WritesConfigJson);
    }

    [Fact]
    public void Build_FailsWhenTwoRoutesClaimTheSamePrefix()
    {
        DockerSiteBuilder builder = new(typeof(CompleteSiteDefinition));
        builder.ProxyApi<OrdersApiDefinition>("/api").ProxyStub<PricingStubDefinition>("/api/");

        FrameworkConfigurationException exception = Assert.Throws<FrameworkConfigurationException>(builder.Build);

        Assert.Contains("'/api'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_FailsWhenARouteProxiesTheRoot()
    {
        DockerSiteBuilder builder = new(typeof(CompleteSiteDefinition));
        builder.ProxyApi<OrdersApiDefinition>("/");

        FrameworkConfigurationException exception = Assert.Throws<FrameworkConfigurationException>(builder.Build);

        Assert.Contains("payload", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProxyApi_FailsWhenThePathDoesNotStartWithASlash()
    {
        DockerSiteBuilder builder = new(typeof(CompleteSiteDefinition));

        Assert.Throws<FrameworkConfigurationException>(() => builder.ProxyApi<OrdersApiDefinition>("api"));
    }

    [Fact]
    public void Build_FailsWhenAValueIsBothWrittenByHandAndBound()
    {
        DockerSiteBuilder builder = new(typeof(CompleteSiteDefinition));
        builder.ConfigJsonApi<OrdersApiDefinition>("apiBaseUrl").WithConfigJsonValue("apiBaseUrl", "http://elsewhere/");

        FrameworkConfigurationException exception = Assert.Throws<FrameworkConfigurationException>(builder.Build);

        Assert.Contains("'apiBaseUrl'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_FailsWhenTwoBindingsTargetTheSamePath()
    {
        DockerSiteBuilder builder = new(typeof(CompleteSiteDefinition));
        builder.ConfigJsonApi<OrdersApiDefinition>("apiBaseUrl").ConfigJsonStub<PricingStubDefinition>("apiBaseUrl");

        Assert.Throws<FrameworkConfigurationException>(builder.Build);
    }

    [Fact]
    public void Build_FailsWhenTheConfigJsonFileIsAlsoComposedByHand()
    {
        DockerSiteBuilder builder = new(typeof(CompleteSiteDefinition));
        builder
            .ConfigJsonApi<OrdersApiDefinition>("apiBaseUrl")
            .WithConfigFile(DockerWebDefaults.SiteConfigJsonPath, _ => "{}");

        FrameworkConfigurationException exception = Assert.Throws<FrameworkConfigurationException>(builder.Build);

        Assert.Contains(DockerWebDefaults.SiteConfigJsonPath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_FailsWhenOneFileIsComposedTwice()
    {
        DockerSiteBuilder builder = new(typeof(CompleteSiteDefinition));
        builder.WithConfigFile("env.js", _ => "a").WithConfigFile("env.js", _ => "b");

        Assert.Throws<FrameworkConfigurationException>(builder.Build);
    }

    [Fact]
    public void ImageSource_RefusesDeclaredServerBehaviour()
    {
        DockerSiteBuilder builder = new(typeof(CompleteSiteDefinition));
        builder.ProxyApi<OrdersApiDefinition>("/api");
        DockerSiteSpec spec = builder.Build();
        SiteSourcePlan plan = new() { Kind = SiteSourceKind.Image, Image = "shop-ui:ci" };

        FrameworkConfigurationException exception = Assert.Throws<FrameworkConfigurationException>(
            () => SiteEnvComponent.EnsureImageSourceServesItself(new DefaultsSiteDefinition(), spec, plan));

        Assert.Contains("as-is", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ImageSource_AllowsGeneratedFiles()
    {
        DockerSiteBuilder builder = new(typeof(CompleteSiteDefinition));
        builder.ConfigJsonApi<OrdersApiDefinition>("apiBaseUrl");
        DockerSiteSpec spec = builder.Build();
        SiteSourcePlan plan = new() { Kind = SiteSourceKind.Image, Image = "shop-ui:ci" };

        SiteEnvComponent.EnsureImageSourceServesItself(new DefaultsSiteDefinition(), spec, plan);
    }

    [Fact]
    public void NpmSource_RefusesAnUnsafeScriptName()
    {
        NpmProjectSiteSource source = SiteSource.NpmProject(AppContext.BaseDirectory);

        Assert.Throws<FrameworkConfigurationException>(() => source.WithBuildScript("build && rm -rf /"));
    }

    [Fact]
    public void ContainerBuildSource_RefusesAnEmptyCommand()
    {
        Assert.Throws<FrameworkConfigurationException>(() => SiteSource.ContainerBuild(AppContext.BaseDirectory, "node:22-alpine", []));
    }

    [Fact]
    public void NpmSource_ResolvesARelativePathAgainstTheDeclaringFile()
    {
        NpmProjectSiteSource source = SiteSource.NpmProject(".");

        Assert.EndsWith("TestFramework.Container.Web.Tests", source.ProjectDirectory, StringComparison.Ordinal);
    }
}
