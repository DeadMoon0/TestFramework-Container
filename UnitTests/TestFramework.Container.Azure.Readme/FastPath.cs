using System.Threading.Tasks;
using TestFramework.Config;
using TestFramework.Container.Azure.Readme.Composition;
using TestFramework.Container.Azure.Readme.Messaging;
using TestFramework.Container.Sources;
using TestFramework.Core.Timelines;

namespace TestFramework.Container.Azure.Readme.FastPath;

internal static class FastPath
{
    public static async Task<TimelineRun> Run(Timeline timeline, ConfigInstance config)
    {
        #region snippet:fast-path
        TimelineRun run = await timeline
            .SetupRun(config)
            .SetEnv(DockerAzureEnvironment.ForFunctionAppWithStorageAndServiceBus<MainStorage, SharedMessaging>(
                "Default",
                ContainerSource.Project("../Processor/Processor.csproj").BuiltOnHost(),
                bus => bus.Incoming,
                bus => bus.Reply))
            .RunAsync();
        #endregion
        return run;
    }
}
