using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Core.Exceptions;
using Xunit;

namespace TestFramework.Container.Tests;

/// <summary>
/// A readiness wait has one clock, and the step it runs in owns it.
/// </summary>
/// <remarks>
/// The waits used to count down on the wall clock beside the step's deadline, and the Cosmos account read could not
/// be cancelled at all - so a wait could run on after the step had been given up, or hang past its own timeout.
/// </remarks>
public class ContainerReadinessTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task TheStepsDeadline_EndsTheWaitBeforeItsOwnTimeout()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromMilliseconds(200));
        Stopwatch elapsed = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ContainerReadiness.WaitUntilAsync(
            static _ => Task.FromResult(false),
            static _ => true,
            TimeSpan.FromMinutes(5),
            static (_, _) => new FrameworkTimeoutException("not ready"),
            deadline.Token));

        Assert.True(elapsed.Elapsed < Generous, $"The wait outlived the deadline by {elapsed.Elapsed}.");
    }

    [Fact]
    public async Task AnAttemptThatHangsAndIgnoresItsToken_EndsWithTheWait()
    {
        // The Cosmos SDK's account read is such an attempt: it takes no token.
        Stopwatch elapsed = Stopwatch.StartNew();

        FrameworkTimeoutException timedOut = await Assert.ThrowsAsync<FrameworkTimeoutException>(() => ContainerReadiness.WaitUntilAsync(
            static _ => new TaskCompletionSource<bool>().Task,
            static _ => true,
            TimeSpan.FromMilliseconds(300),
            static (_, _) => new FrameworkTimeoutException("the gateway did not answer"),
            CancellationToken.None));

        Assert.StartsWith("the gateway did not answer", timedOut.Message, StringComparison.Ordinal);
        Assert.True(elapsed.Elapsed < Generous, $"A hanging attempt held the wait for {elapsed.Elapsed}.");
    }

    [Fact]
    public async Task AWaitThatRunsOut_SaysHowOftenItTriedAndWhatFailedLast()
    {
        IOException notYet = new("connection refused");

        FrameworkTimeoutException timedOut = await Assert.ThrowsAsync<FrameworkTimeoutException>(() => ContainerReadiness.WaitUntilAsync(
            _ => Task.FromException<bool>(notYet),
            static exception => exception is IOException,
            TimeSpan.FromMilliseconds(1500),
            static (attempts, last) => new FrameworkTimeoutException($"{attempts} attempts, last: {last?.Message}", last),
            CancellationToken.None));

        Assert.Matches(@"^[1-9]\d* attempts, last: connection refused", timedOut.Message);
        Assert.Same(notYet, timedOut.InnerException);
    }

    [Fact]
    public async Task AFailureThatIsNotTransient_EndsTheWaitAtOnce()
    {
        Stopwatch elapsed = Stopwatch.StartNew();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ContainerReadiness.WaitUntilAsync(
            static _ => Task.FromException<bool>(new UnauthorizedAccessException("wrong key")),
            static exception => exception is IOException,
            TimeSpan.FromMinutes(5),
            static (_, _) => new FrameworkTimeoutException("not ready"),
            CancellationToken.None));

        Assert.True(elapsed.Elapsed < Generous);
    }

    [Fact]
    public async Task AServiceThatBecomesReady_EndsTheWaitThen()
    {
        int attempts = 0;

        await ContainerReadiness.WaitUntilAsync(
            _ => Task.FromResult(++attempts == 2),
            static _ => true,
            TimeSpan.FromMinutes(1),
            static (_, _) => new FrameworkTimeoutException("not ready"),
            CancellationToken.None);

        Assert.Equal(2, attempts);
    }
}
