using System;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Core.Logging;

namespace TestFramework.Container;

/// <summary>
/// Makes sure Docker can run this package's containers before the first one is asked for.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ContainerDockerHost.EnsureConfigured"/> exists because Docker Desktop on Windows listens
/// on a named pipe whose name differs between installations and the client library does not probe for
/// it. Until now only this repository's own test projects called it, from a module initializer, so
/// every consumer of the package met exactly the failure it was written to prevent: a machine with a
/// working Docker installation that cannot connect, for no visible reason.
/// </para>
/// <para>
/// A module initializer in the library itself would be worse than the disease. It fires the first time
/// any type in the assembly is touched — a reflection scan is enough — and it would mutate
/// <c>DOCKER_HOST</c> for the whole process as a load-time side effect, with no logger in scope to say
/// that it had. Calling this at the top of the network component instead puts the change at a point
/// where the run has already decided it wants containers and has somewhere to write about it.
/// </para>
/// </remarks>
public static class ContainerRuntime
{
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim ProbeGate = new(1, 1);
    private static bool _initialized;
    private static bool _ready;
    private static string? _configuredHost;

    /// <summary>
    /// Points the Docker client at the engine this machine runs and asks that engine whether it can serve this
    /// package's containers.
    /// </summary>
    /// <param name="cancellationToken">What stops the probe.</param>
    /// <exception cref="TestFramework.Core.Exceptions.FrameworkConfigurationException">No Docker CLI, no daemon answering, or a
    /// daemon serving Windows containers - each refused by name.</exception>
    public static Task EnsureReadyAsync(CancellationToken cancellationToken)
        => EnsureReadyAsync(null, cancellationToken);

    /// <summary>
    /// Points the Docker client at the engine this machine runs and asks that engine whether it can serve this
    /// package's containers.
    /// </summary>
    /// <remarks>
    /// Safe to call from several component creations at once. The host is configured once per process; the probe
    /// runs until it first succeeds, so a run that started Docker after a refusal is not refused again.
    /// </remarks>
    /// <param name="logger">Optional logger; the chosen pipe and the probe's answer are reported through it.</param>
    /// <param name="cancellationToken">What stops the probe.</param>
    /// <exception cref="TestFramework.Core.Exceptions.FrameworkConfigurationException">No Docker CLI, no daemon answering, or a
    /// daemon serving Windows containers - each refused by name.</exception>
    public static async Task EnsureReadyAsync(ScopedLogger? logger, CancellationToken cancellationToken)
    {
        ConfigureHost(logger);

        if (Volatile.Read(ref _ready))
            return;

        await ProbeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ready)
                return;

            await DockerProbe.RequireAsync(logger, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _ready, true);

            // Housekeeping needs a daemon, so it waits for the first answer.
            StartHousekeeping(logger);
        }
        finally
        {
            ProbeGate.Release();
        }
    }

    private static void ConfigureHost(ScopedLogger? logger)
    {
        lock (Gate)
        {
            if (_initialized)
            {
                if (_configuredHost is { } alreadyConfigured)
                    logger?.LogInformation($"The Docker client is pointed at '{alreadyConfigured}'.");

                return;
            }

            _initialized = true;

            try
            {
                _configuredHost = ContainerDockerHost.EnsureConfigured();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Probing for a pipe must never be the thing that fails a run. Whatever the client
                // does next will fail with a message about Docker rather than about this.
                logger?.LogWarning($"The Docker host could not be probed: {exception.Message}");
                return;
            }

            if (_configuredHost is { } configured)
                logger?.LogInformation($"DOCKER_HOST was not set, so the Docker client was pointed at '{configured}'.");
        }
    }

    /// <summary>
    /// Starts the leftover sweep without waiting for it.
    /// </summary>
    /// <remarks>
    /// It runs here because this is the earliest point at which the Docker client knows where the daemon
    /// is, and it runs detached because a run must not wait on housekeeping. Anything it throws is
    /// swallowed inside <see cref="ContainerLeftovers.SweepAsync"/>; the extra continuation is there for
    /// the case where scheduling itself fails.
    /// </remarks>
    private static void StartHousekeeping(ScopedLogger? logger)
    {
        if (ContainerLeftovers.IsDisabled())
        {
            logger?.LogInformation($"Housekeeping is off because {ContainerLeftovers.NoSweepVariable} is set.");
            return;
        }

        try
        {
            _ = Task.Run(() => ContainerLeftovers.SweepAsync(logger, CancellationToken.None))
                .ContinueWith(static _ => { }, TaskScheduler.Default);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger?.LogWarning($"Housekeeping could not be started: {exception.Message}");
        }
    }

    /// <summary>
    /// Resets the one-time state. For tests that need to observe initialization again.
    /// </summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _initialized = false;
            _ready = false;
            _configuredHost = null;
        }
    }
}
