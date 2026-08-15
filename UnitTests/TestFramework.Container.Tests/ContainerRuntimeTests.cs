using Xunit;

namespace TestFramework.Container.Tests;

/// <summary>
/// Covers the initialization every consumer used to have to do for itself.
/// </summary>
public class ContainerRuntimeTests
{
    [Fact]
    public void EnsureInitialized_IsSafeToCallRepeatedly()
    {
        // Both network components call it, and a run may create several environments.
        ContainerRuntime.EnsureInitialized();
        ContainerRuntime.EnsureInitialized();
        ContainerRuntime.EnsureInitialized(null);
    }
}
