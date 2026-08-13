using System.Runtime.CompilerServices;

namespace TestFramework.Container.Azure.Tests;

internal static class TestRuntimeSetup
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        ContainerDockerHost.EnsureConfigured();
    }
}
