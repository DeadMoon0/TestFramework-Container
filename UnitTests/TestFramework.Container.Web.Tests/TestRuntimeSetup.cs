using System.Runtime.CompilerServices;

namespace TestFramework.Container.Web.Tests;

internal static class TestRuntimeSetup
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        ContainerDockerHost.EnsureConfigured();
    }
}
