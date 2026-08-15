using System;
using TestFramework.Core.Logging;

namespace TestFramework.Container;

/// <summary>
/// Makes the Docker client usable before the first container is asked for.
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
    private static bool _initialized;
    private static string? _configuredHost;

    /// <summary>
    /// Points the Docker client at the engine this machine runs, once per process.
    /// </summary>
    /// <param name="logger">Optional logger; the chosen pipe is reported through it when one is set.</param>
    /// <remarks>
    /// Idempotent, and safe to call from several component creations at once. It changes nothing when
    /// <c>DOCKER_HOST</c> is already set or when the platform has no named pipes.
    /// </remarks>
    public static void EnsureInitialized(ScopedLogger? logger = null)
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
    /// Resets the one-time state. For tests that need to observe initialization again.
    /// </summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _initialized = false;
            _configuredHost = null;
        }
    }
}
