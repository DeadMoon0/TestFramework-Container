using System.Threading.Tasks;
using TestFramework.Config;
using TestFramework.Core.Timelines;
using TestFramework.Web.Extensions;
using Xunit;

namespace TestFramework.Container.Web.Readme.Hosted;

#region snippet:hosted-collection
public sealed class OrdersStack : IDockerWebHostedFixtureState
{
    public DockerWebEnvironment CreateEnvironment() => DockerWebEnvironment.For<OrdersDatabase>();

    public ConfigInstance CreatePersistentConfig() => ConfigInstance.Create().LoadWebConfig().Build();
}

public sealed class OrdersFixture : DockerWebHostedCollectionFixture<OrdersStack>, IAsyncLifetime;
#endregion

internal static class HostedRun
{
    public static async Task<TimelineRun> Run(Timeline timeline, ConfigInstance config, OrdersFixture fixture)
    {
        #region snippet:hosted-collection
        // in a test of the collection
        TimelineRun run = await timeline.SetupRun(config).SetEnv(fixture.GetEnv()).RunAsync();
        #endregion
        return run;
    }
}
