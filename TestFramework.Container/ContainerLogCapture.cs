using System;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Testcontainers.Containers;
using TestFramework.Core.Logging;

namespace TestFramework.Container;

/// <summary>
/// Writes a container's output into the run log before it is removed.
/// </summary>
/// <remarks>
/// Once a container is gone its log is gone with it, and a failure inside the container is otherwise
/// invisible from the test side. Capturing must never fail teardown, so every problem here is logged
/// rather than thrown.
/// </remarks>
public static class ContainerLogCapture
{
    /// <summary>
    /// Writes standard output and standard error of a container into the run log.
    /// </summary>
    /// <param name="container">The container to read from.</param>
    /// <param name="description">A description used in log output, such as the identifier.</param>
    /// <param name="logger">The scoped logger.</param>
    /// <param name="cancellationToken">The cancellation token for the running teardown.</param>
    public static async Task CaptureAsync(IContainer container, string description, ScopedLogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            (string standardOutput, string standardError) = await container.GetLogsAsync(
                since: DateTime.UnixEpoch,
                until: DateTime.UtcNow,
                timestampsEnabled: false,
                ct: cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(standardOutput))
                logger.LogInformation($"{description} stdout ({container.Id}):{Environment.NewLine}{standardOutput}");

            if (!string.IsNullOrWhiteSpace(standardError))
                logger.LogWarning($"{description} stderr ({container.Id}):{Environment.NewLine}{standardError}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning($"Failed to capture {description} container logs ({container.Id}): {exception.GetType().Name}: {exception.Message}");
        }
    }
}
