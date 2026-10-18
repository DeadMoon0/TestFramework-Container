using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace TestFramework.Container.Tests;

/// <summary>
/// Containers of one component start together, come back in the order they were declared, and none is left
/// running when one of them fails.
/// </summary>
/// <remarks>
/// The coordinator exists for its failure path: a component that throws halfway never returns state, so nothing
/// else will ever tear down what it started. No Docker here - the containers are stand-ins that record whether
/// they were taken down.
/// </remarks>
public class ContainerStartCoordinatorTests
{
    [Fact]
    public async Task ResultsComeBackInTheOrderTheyWereDeclared_WhateverOrderTheyFinishedIn()
    {
        int[] delays = [300, 10, 150, 0];

        IReadOnlyList<int> results = await ContainerStartCoordinator.StartAllAsync(
            [0, 1, 2, 3],
            async (item, token) =>
            {
                await Task.Delay(delays[item], token);
                return item;
            },
            static _ => null,
            null,
            CancellationToken.None);

        Assert.Equal([0, 1, 2, 3], results);
    }

    [Fact]
    public async Task WhenOneStartFails_EveryContainerThatStartedIsTakenDown_AndThatFailureIsTheOneReported()
    {
        InvalidOperationException failure = new("the third container never became ready");
        StandIn[] containers = [StandIn.Create(), StandIn.Create(), StandIn.Create()];

        InvalidOperationException reported = await Assert.ThrowsAsync<InvalidOperationException>(() => ContainerStartCoordinator.StartAllAsync(
            [0, 1, 2],
            async (item, token) =>
            {
                if (item == 2)
                {
                    // Late enough that the other two have come up.
                    await Task.Delay(200, token);
                    throw failure;
                }

                return containers[item];
            },
            static container => (IContainer)container,
            null,
            CancellationToken.None));

        Assert.Same(failure, reported);
        Assert.True(StandIn.Of(containers[0]).TakenDown, "The first container was left running.");
        Assert.True(StandIn.Of(containers[1]).TakenDown, "The second container was left running.");
    }

    [Fact]
    public async Task WhenOneStartFails_TheStartsStillWaitingStopPayingForTheirTimeouts()
    {
        TaskCompletionSource<bool> siblingCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ContainerStartCoordinator.StartAllAsync(
            [0, 1],
            async (item, token) =>
            {
                if (item == 0)
                    throw new InvalidOperationException("failed at once");

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), token);
                }
                catch (OperationCanceledException)
                {
                    siblingCancelled.TrySetResult(true);
                    throw;
                }

                return item;
            },
            static _ => null,
            null,
            CancellationToken.None));

        Assert.True(await siblingCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task NoMoreStartAtOnceThanTheComponentAllows()
    {
        int running = 0;
        int highest = 0;

        await ContainerStartCoordinator.StartAllAsync(
            Enumerable.Range(0, 10).ToList(),
            async (item, token) =>
            {
                int now = Interlocked.Increment(ref running);
                InterlockedMax(ref highest, now);
                await Task.Delay(50, token);
                Interlocked.Decrement(ref running);
                return item;
            },
            static _ => null,
            null,
            maxConcurrency: 3,
            CancellationToken.None);

        Assert.InRange(highest, 1, 3);
    }

    [Fact]
    public async Task TheCallerCancelling_IsWhatIsReported_NotAStartsReactionToIt()
    {
        using CancellationTokenSource caller = new();
        StandIn container = StandIn.Create();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ContainerStartCoordinator.StartAllAsync(
            [0, 1],
            async (item, token) =>
            {
                if (item == 0)
                    return container;

                await caller.CancelAsync();
                throw new InvalidOperationException("a start that failed because the run was stopped");
            },
            static started => (IContainer)started,
            null,
            caller.Token));

        Assert.True(StandIn.Of(container).TakenDown, "A container that had started was left running after the caller stopped.");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    /// <summary>
    /// A container that only records whether it was taken down.
    /// </summary>
    /// <remarks>
    /// Removal goes through <c>docker rm -f &lt;id&gt;</c>, which Docker answers with success for an id that names
    /// nothing, and falls back to disposing only when the CLI fails. So both count: the id being read, which only
    /// the removal does, and the container being disposed.
    /// </remarks>
    public class StandIn : DispatchProxy
    {
        public bool TakenDown { get; private set; }

        public string Id { get; } = $"tf-stand-in-{Guid.NewGuid():N}";

        public static StandIn Create()
            => (StandIn)(object)Create<IContainer, StandIn>();

        public static StandIn Of(StandIn container) => container;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "get_Id":
                    TakenDown = true;
                    return Id;
                case nameof(IAsyncDisposable.DisposeAsync):
                    TakenDown = true;
                    return ValueTask.CompletedTask;
                default:
                    throw new NotSupportedException(targetMethod?.Name);
            }
        }
    }
}
