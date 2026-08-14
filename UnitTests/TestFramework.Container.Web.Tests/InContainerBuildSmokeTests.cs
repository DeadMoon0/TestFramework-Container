using System;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Config;
using TestFramework.Container.Sources;
using TestFramework.Core.Timelines;
using TestFramework.Core.Timelines.Assertions;
using TestFramework.Web;
using TestFramework.Web.Extensions;
using TestFramework.Web.Identifier;
using TestFramework.Web.Trigger.IsLive;
using Xunit;

namespace TestFramework.Container.Web.Tests;

/// <summary>
/// Proves an application can be built inside Docker with no host build output and no feed
/// credentials.
/// </summary>
/// <remarks>
/// Needs a Docker daemon and pulls the .NET SDK image on first use, so it is excluded from the
/// default run. Run with <c>--filter "Category=DockerSmoke"</c>.
/// </remarks>
[Trait("Category", "DockerSmoke")]
public class InContainerBuildSmokeTests
{
    private sealed class SelfBuiltApiDefinition : DockerApiDefinition
    {
        public override ApiIdentifier Identifier => "self-built";

        // Built for the generation of the SDK on this machine, because an offline build hands the
        // container the packages that SDK resolved; a different generation resolves a different set.
        public override ContainerSource Source =>
            ContainerSource.Project("../TestFramework.Container.Web.SampleApi/TestFramework.Container.Web.SampleApi.csproj")
                .WithTargetFramework(HostSdkTargetFramework)
                .BuiltInContainer();

        protected override void Configure(DockerApiBuilder builder) => builder.WithHealthPath("/health");
    }

    /// <summary>
    /// The framework generation of the SDK on this machine, which is the only one an offline
    /// in-container build can produce.
    /// </summary>
    private static string HostSdkTargetFramework
    {
        get
        {
            string? sdk = DotNetCli.ReadSdkMajorMinorAsync(CancellationToken.None).GetAwaiter().GetResult();
            return sdk is null ? "net10.0" : $"net{sdk.Split('.')[0]}.0";
        }
    }

    [Fact]
    public async Task ApplicationBuiltInsideDocker_Answers()
    {
        Timeline timeline = Timeline.Create()
            .Trigger(WebExt.Api.IsLive("self-built", ApiAlivenessLevel.Healthy))
                .WithTimeOut(TimeSpan.FromMinutes(5)).Name("live")
            .Build();

        TimelineRun run = await timeline.SetupRun(ConfigInstance.Create().LoadWebConfig().Build())
            .SetEnv(DockerWebEnvironment.For<SelfBuiltApiDefinition>())
            .RunAsync();

        run.EnsureRanToCompletion();
        run.ApiProbe("live").Select(probe => probe.Success).Should().Be(true);

        ApiComponentState? state = run.EnvironmentContext.GetState<ApiComponentState>(DockerWebEnvironment.ApiComponentId);
        Assert.NotNull(state);
        RunningApi api = state!.GetRequiredApi("self-built");

        Assert.Equal(ContainerBuildStrategy.InContainer, api.Plan.Strategy);
        Assert.StartsWith("mcr.microsoft.com/dotnet/sdk:", api.Plan.SdkImage!, StringComparison.Ordinal);
        Assert.StartsWith("testframework-self-built:", api.Plan.Image!, StringComparison.Ordinal);

        // The whole point: the packages came from the host's restore, so the build inside the
        // container never needed a feed or a credential.
        Assert.NotNull(api.Plan.ContextDirectory);
    }
}
