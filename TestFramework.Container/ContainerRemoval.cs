using System;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Testcontainers.Containers;
using TestFramework.Core.Logging;

namespace TestFramework.Container;

/// <summary>
/// Removes a container that failed on its way to its owner.
/// </summary>
/// <remarks>
/// A failure to remove it never replaces the failure that got it here, but it is said: a container that could
/// be neither handed over nor removed keeps running, and the only trace of it would otherwise be Docker's own
/// list.
/// </remarks>
internal static class ContainerRemoval
{
    internal static async Task RemoveAfterFailureAsync(IContainer container, ScopedLogger? logger)
    {
        // Not the step's token: when bringing the container up was cancelled, removing it is exactly what
        // still has to happen.
        try
        {
            await ContainerDockerCommands.ForceRemoveContainerAsync(container, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger?.LogWarning("Container '{0}' failed to come up and could not be removed, so it may still be running: {1}", container.Id, exception.Message);
        }

        try
        {
            await container.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger?.LogWarning("Container '{0}' could not be disposed after it failed to come up: {1}", container.Id, exception.Message);
        }
    }
}
