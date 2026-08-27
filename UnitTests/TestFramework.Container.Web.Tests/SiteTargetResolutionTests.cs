using System;
using Microsoft.Extensions.DependencyInjection;
using TestFramework.Config;
using TestFramework.Container.Web.Components;
using TestFramework.Core.Exceptions;
using TestFramework.Core.Steps;
using TestFramework.Web.Extensions;
using Xunit;

namespace TestFramework.Container.Web.Tests;

/// <summary>
/// Where a site's proxy routes and bindings point when the backend is not containerized.
/// </summary>
/// <remarks>
/// <para>
/// A site definition is meant to serve a containerized backend and a deployed one without changing, so this
/// branch - nothing in the environment declares the target, so the run is asked - is half of that promise
/// and had no case of its own. It reached a configuration store until the store stopped being how a run
/// answers for its resources.
/// </para>
/// <para>
/// Driven through the resolver rather than a container, because what is under test is the choice of address,
/// and starting nginx to read it back would test the parts that already have smoke coverage.
/// </para>
/// </remarks>
public class SiteTargetResolutionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADeployedBackendIsFoundFromWhatTheRunHolds(bool networkFacing)
    {
        using ServiceProvider services = ConfigInstance.Create()
            .LoadWebConfig()
            .OverrideConfig("Api:orders:BaseUrl", "https://orders.example.test/")
            .BuildServiceProvider();

        // No definitions, so nothing is containerized and the resolver has only the run to go on.
        SiteEnvComponent.SiteTargetResolver resolver = new(new DockerWebEnvironment(), RunContext.Detached(services));

        Uri resolved = resolver.Resolve(SiteTargetKind.Api, "orders", networkFacing);

        // Both viewpoints, and the same answer: an entry somebody wrote down is published for both, which is
        // exactly why this branch could get away with holding one address. Asking is still the right shape -
        // the caller says which side it is on, and whatever supplies the resource decides whether that
        // matters.
        Assert.Equal("https://orders.example.test/", resolved.ToString());
    }

    [Fact]
    public void ABackendNothingSuppliesSaysSoRatherThanGuessing()
    {
        using ServiceProvider services = ConfigInstance.Create().LoadWebConfig().BuildServiceProvider();

        SiteEnvComponent.SiteTargetResolver resolver = new(new DockerWebEnvironment(), RunContext.Detached(services));

        FrameworkConfigurationException refused = Assert.Throws<FrameworkConfigurationException>(
            () => resolver.Resolve(SiteTargetKind.Api, "orders", networkFacing: true));

        Assert.Contains("no included definition declares", refused.Message, StringComparison.Ordinal);
        Assert.Contains("configure 'Api:orders:BaseUrl'", refused.ToString(), StringComparison.Ordinal);
    }

}
