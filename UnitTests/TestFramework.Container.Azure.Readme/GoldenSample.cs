#region snippet:golden-sample
using TestFramework.Azure;
using TestFramework.Azure.Extensions;
using TestFramework.Azure.Identifier;
using TestFramework.Config;
using TestFramework.Container.Azure;
using TestFramework.Core.Timelines;
using Xunit;
#endregion
using System.Threading.Tasks;

namespace TestFramework.Container.Azure.Readme.GoldenSample;

#region snippet:golden-sample
public class ContainerAzureSample
{
    private sealed class OrdersStorage : DockerStorageAccountDefinition
    {
        public override StorageAccountIdentifier Identifier => "orders";

        // The definition names its blob container, so the run needs no configuration entry for it.
        protected override string? BlobContainerName => "incoming";
    }

    [Fact]
    public async Task Timeline_runs_against_an_emulated_storage_account()
    {
        Timeline timeline = Timeline.Create()
            .Trigger(AzureExt.Trigger.IsLive.Blob("orders", AlivenessLevel.Authenticated))
            .Build();

        TimelineRun run = await timeline
            .SetupRun(ConfigInstance.Create().LoadDockerAzureConfig().Build())
            .SetEnv(DockerAzureEnvironment.For<OrdersStorage>())
            .RunAsync();

        run.EnsureRanToCompletion();
        Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.AzuriteComponentId));
    }
}
#endregion
