using System;
using System.Threading.Tasks;
using DotNet.Testcontainers.Containers;
using TestFramework.Core.Logging;

namespace TestFramework.Container;

/// <summary>
/// Brings one container up, and takes it away again if it cannot be handed to its owner.
/// </summary>
/// <remarks>
/// <para>
/// An environment component tears down only what it returned. A container that started and then failed
/// on the way to being returned - its readiness probe timed out, a guard refused its address, its schema
/// did not apply - belongs to nobody: the component threw, so the framework has nothing to tear down, and
/// the container keeps running until the test host exits, or for good wherever the reaper is switched off.
/// </para>
/// <para>
/// So everything between building a container and handing it over runs through here, start included: a
/// start that fails halfway can still leave a created container behind. What was started is removed before
/// the original failure is rethrown; a failure to remove it is logged, never thrown in its place.
/// </para>
/// </remarks>
public static class ContainerStartup
{
    /// <summary>
    /// Runs <paramref name="bringUp"/> for <paramref name="container"/> and removes the container when it throws.
    /// </summary>
    /// <typeparam name="TResult">What the component hands over once the container is usable.</typeparam>
    /// <param name="container">The container being brought up. Removed if <paramref name="bringUp"/> fails.</param>
    /// <param name="logger">Told when the container could not be removed.</param>
    /// <param name="bringUp">Starts the container and makes it usable: start, readiness, guards, whatever else comes before handing it over.</param>
    /// <returns>What <paramref name="bringUp"/> returned.</returns>
    public static async Task<TResult> BringUpAsync<TResult>(IContainer container, ScopedLogger? logger, Func<Task<TResult>> bringUp)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(bringUp);

        try
        {
            return await bringUp().ConfigureAwait(false);
        }
        catch
        {
            await ContainerRemoval.RemoveAfterFailureAsync(container, logger).ConfigureAwait(false);
            throw;
        }
    }
}
