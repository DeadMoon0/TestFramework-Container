using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Testcontainers.Containers;

namespace TestFramework.Container;

/// <summary>
/// Starts several containers of one environment component at the same time, and cleans up after itself
/// when one of them fails.
/// </summary>
/// <remarks>
/// <para>
/// An environment component that owns more than one container used to start them one after another and
/// wait out each readiness probe before touching the next. Two applications with a one-minute startup
/// cost two minutes for no reason: the containers do not depend on each other, only on the component
/// that owns them.
/// </para>
/// <para>
/// The failure behaviour matters more than the speed. A serial loop that throws halfway already leaked
/// every container it had started, because the component never returned state for the framework to tear
/// down. Racing the starts would multiply that leak, so a failure here cancels the remaining starts and
/// force-removes whatever did come up before rethrowing the original error.
/// </para>
/// <para>
/// The results come back in the order the items were passed in, whatever order the starts finished in,
/// so a caller that ordered its identifiers deterministically gets a deterministic container list and a
/// deterministic published configuration. Anything that is not safe to race — writing to a config store,
/// for one, whose creation is locked but whose writes are not — belongs in a tail loop over that result.
/// </para>
/// </remarks>
public static class ContainerStartCoordinator
{
    /// <summary>
    /// How many containers of one component start at the same time unless the caller says otherwise.
    /// </summary>
    /// <remarks>
    /// Docker pulls, extracts and starts on the same daemon, and a test host is rarely alone on the
    /// machine. Four is enough to hide the readiness waits behind each other without turning the daemon
    /// into the bottleneck.
    /// </remarks>
    public const int DefaultMaxConcurrency = 4;

    /// <summary>
    /// Runs a start operation for every item concurrently and returns the results in input order.
    /// </summary>
    /// <typeparam name="TItem">The declaration a single container is started from.</typeparam>
    /// <typeparam name="TResult">What a completed start produces.</typeparam>
    /// <param name="items">The declarations, in the order the results should come back in.</param>
    /// <param name="startAsync">Starts one container and waits for it to be usable.</param>
    /// <param name="containerSelector">Reads the container out of a result so a failed run can remove it.</param>
    /// <param name="maxConcurrency">How many starts may be in flight at once.</param>
    /// <param name="cancellationToken">The cancellation token for the running setup.</param>
    /// <returns>One result per item, in the order the items were given.</returns>
    public static async Task<IReadOnlyList<TResult>> StartAllAsync<TItem, TResult>(
        IReadOnlyList<TItem> items,
        Func<TItem, CancellationToken, Task<TResult>> startAsync,
        Func<TResult, IContainer?> containerSelector,
        int maxConcurrency,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(startAsync);
        ArgumentNullException.ThrowIfNull(containerSelector);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);

        if (items.Count == 0)
            return [];

        TResult[] results = new TResult[items.Count];
        bool[] started = new bool[items.Count];

        using CancellationTokenSource abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using SemaphoreSlim gate = new(maxConcurrency, maxConcurrency);

        Task[] starts = new Task[items.Count];
        for (int index = 0; index < items.Count; index++)
        {
            int position = index;
            starts[position] = StartOneAsync(position);
        }

        try
        {
            await Task.WhenAll(starts).ConfigureAwait(false);
            return results;
        }
        catch
        {
            // Whatever came up before the failure is nobody's to dispose: the component never returned
            // state, so the framework will not tear it down.
            await RemoveStartedAsync(results, started, containerSelector).ConfigureAwait(false);
            Rethrow(starts, cancellationToken);
            throw;
        }

        async Task StartOneAsync(int position)
        {
            // Yielding first keeps the loop above from running the first item to completion inline.
            await Task.Yield();
            await gate.WaitAsync(abort.Token).ConfigureAwait(false);
            try
            {
                results[position] = await startAsync(items[position], abort.Token).ConfigureAwait(false);
                started[position] = true;
            }
            catch
            {
                // One failure ends the whole component, so the siblings should stop paying for their
                // readiness timeouts.
                await abort.CancelAsync().ConfigureAwait(false);
                throw;
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>
    /// Runs a start operation for every item concurrently at the default concurrency.
    /// </summary>
    /// <typeparam name="TItem">The declaration a single container is started from.</typeparam>
    /// <typeparam name="TResult">What a completed start produces.</typeparam>
    /// <param name="items">The declarations, in the order the results should come back in.</param>
    /// <param name="startAsync">Starts one container and waits for it to be usable.</param>
    /// <param name="containerSelector">Reads the container out of a result so a failed run can remove it.</param>
    /// <param name="cancellationToken">The cancellation token for the running setup.</param>
    /// <returns>One result per item, in the order the items were given.</returns>
    public static Task<IReadOnlyList<TResult>> StartAllAsync<TItem, TResult>(
        IReadOnlyList<TItem> items,
        Func<TItem, CancellationToken, Task<TResult>> startAsync,
        Func<TResult, IContainer?> containerSelector,
        CancellationToken cancellationToken)
        => StartAllAsync(items, startAsync, containerSelector, DefaultMaxConcurrency, cancellationToken);

    private static async Task RemoveStartedAsync<TResult>(TResult[] results, bool[] started, Func<TResult, IContainer?> containerSelector)
    {
        for (int index = 0; index < results.Length; index++)
        {
            if (!started[index])
                continue;

            IContainer? container = containerSelector(results[index]);
            if (container is null)
                continue;

            try
            {
                // Cleanup after a failure must never replace the failure with one of its own.
                await ContainerDockerCommands.ForceRemoveContainerAsync(container, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Rethrows the failure that actually ended the component rather than a sibling's cancellation.
    /// </summary>
    private static void Rethrow(Task[] starts, CancellationToken cancellationToken)
    {
        // The caller cancelling outranks anything the starts reported.
        cancellationToken.ThrowIfCancellationRequested();

        Exception? cancellation = null;
        foreach (Task start in starts)
        {
            if (start.Exception is not AggregateException aggregate)
                continue;

            foreach (Exception candidate in aggregate.Flatten().InnerExceptions)
            {
                if (candidate is OperationCanceledException)
                {
                    cancellation ??= candidate;
                    continue;
                }

                ExceptionDispatchInfo.Capture(candidate).Throw();
            }
        }

        if (cancellation is not null)
            ExceptionDispatchInfo.Capture(cancellation).Throw();
    }
}
