using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Core.Logging;

namespace TestFramework.Container;

/// <summary>
/// Removes what a killed test host leaves behind.
/// </summary>
/// <remarks>
/// <para>
/// Ryuk reaps containers, networks and volumes, and nothing else. A run that is killed rather than torn
/// down therefore leaves its built images, its published output and its generated emulator topology on
/// the machine, and there is no later run that knows about them. On a developer's machine that adds up
/// to gigabytes over a few weeks of a red build.
/// </para>
/// <para>
/// One case is deliberate rather than accidental: an in-container build keeps its context after a
/// failure so the generated Dockerfile and the offline feed can be inspected. That is good reasoning and
/// exactly why the context needs an expiry from outside — the thing that decided to keep it is the thing
/// that just failed.
/// </para>
/// <para>
/// Housekeeping is best-effort by construction. It never throws, it never blocks a run, it only ever
/// looks at resources this framework labelled or named, and <c>TESTFRAMEWORK_CONTAINER_NO_SWEEP</c>
/// turns it off.
/// </para>
/// </remarks>
public static class ContainerLeftovers
{
    /// <summary>
    /// Set this to any non-empty value to turn housekeeping off.
    /// </summary>
    public const string NoSweepVariable = "TESTFRAMEWORK_CONTAINER_NO_SWEEP";

    /// <summary>
    /// The label put on images built from a generated Dockerfile.
    /// </summary>
    public const string BuildLabel = "com.testframework.container";

    /// <summary>
    /// The value of <see cref="BuildLabel"/>.
    /// </summary>
    public const string BuildLabelValue = "true";

    /// <summary>
    /// The label an SDK-published image carries instead.
    /// </summary>
    /// <remarks>
    /// <c>ContainerLabel</c> is an MSBuild item, and an item cannot be added from the command line. The
    /// SDK does expose <c>ContainerVendor</c> as a property, and it writes the OCI vendor annotation, so
    /// that is the one label an SDK publish can be given with a <c>-p:</c> switch.
    /// </remarks>
    public const string VendorLabel = "org.opencontainers.image.vendor";

    /// <summary>
    /// The value of <see cref="VendorLabel"/> on images this framework publishes.
    /// </summary>
    public const string VendorLabelValue = "testframework-container";

    /// <summary>
    /// The prefix of the temporary directories a build or a publish creates.
    /// </summary>
    public const string TempPrefix = "tf-";

    private const string ExpiryFilter = "24h";
    private static readonly TimeSpan Expiry = TimeSpan.FromHours(24);

    /// <summary>
    /// Removes labelled images and framework-owned temporary files older than a day.
    /// </summary>
    /// <param name="logger">Optional logger; a summary is written to it when anything was removed.</param>
    /// <param name="cancellationToken">The cancellation token for the running sweep.</param>
    /// <remarks>
    /// Never throws. A machine with no Docker, a locked file or a permission error all end the sweep
    /// quietly, because nothing here is worth failing a run over.
    /// </remarks>
    public static async Task SweepAsync(ScopedLogger? logger, CancellationToken cancellationToken)
    {
        if (IsDisabled())
            return;

        try
        {
            await PruneImagesAsync(logger, cancellationToken).ConfigureAwait(false);
            SweepTemporaryDirectories(logger);
            SweepTopologyFiles(logger);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            TryLog(logger, $"Housekeeping was skipped: {exception.Message}");
        }
    }

    /// <summary>
    /// Whether the caller turned housekeeping off.
    /// </summary>
    public static bool IsDisabled()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(NoSweepVariable));

    /// <summary>
    /// The directory the Service Bus emulator topologies this framework generates are written to.
    /// </summary>
    public static string TopologyDirectory
        => Path.Combine(Path.GetTempPath(), "TestFramework", "servicebus-topologies");

    private static async Task PruneImagesAsync(ScopedLogger? logger, CancellationToken cancellationToken)
    {
        // 'until' is measured against the image's creation time, not its last use, so an image built by
        // a run that lasted longer than a day is eligible while that run is still using it. Docker
        // refuses to remove an image a container is running from, so the prune skips it rather than
        // breaking the run.
        await PruneAsync($"{BuildLabel}={BuildLabelValue}", logger, cancellationToken).ConfigureAwait(false);
        await PruneAsync($"{VendorLabel}={VendorLabelValue}", logger, cancellationToken).ConfigureAwait(false);
    }

    private static async Task PruneAsync(string labelFilter, ScopedLogger? logger, CancellationToken cancellationToken)
    {
        try
        {
            ContainerDockerCommands.CommandResult result = await ContainerDockerCommands
                .RunAsync($"image prune -f --filter \"label={labelFilter}\" --filter \"until={ExpiryFilter}\"", cancellationToken)
                .ConfigureAwait(false);

            if (result.ExitCode == 0 && result.StandardOutput.Contains("Deleted", StringComparison.OrdinalIgnoreCase))
                TryLog(logger, $"Housekeeping removed images labelled '{labelFilter}' older than {ExpiryFilter}.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // No Docker CLI, no daemon, a wedged command — none of it matters here.
            TryLog(logger, $"Housekeeping could not prune images labelled '{labelFilter}': {exception.Message}");
        }
    }

    private static void SweepTemporaryDirectories(ScopedLogger? logger)
    {
        // Only the top level of the temp directory, and only the framework's own prefix. Widening
        // either of those turns housekeeping into something that deletes other people's work.
        List<string> removed = [];
        DateTime cutoff = DateTime.UtcNow - Expiry;

        foreach (string directory in EnumerateSafely(Path.GetTempPath(), $"{TempPrefix}*", searchDirectories: true))
        {
            if (Directory.GetLastWriteTimeUtc(directory) > cutoff)
                continue;

            if (TryDelete(() => Directory.Delete(directory, recursive: true)))
                removed.Add(Path.GetFileName(directory));
        }

        if (removed.Count > 0)
            TryLog(logger, $"Housekeeping removed {removed.Count} leftover build director{(removed.Count == 1 ? "y" : "ies")} older than {ExpiryFilter}.");
    }

    private static void SweepTopologyFiles(ScopedLogger? logger)
    {
        string directory = TopologyDirectory;
        if (!Directory.Exists(directory))
            return;

        int removed = 0;
        DateTime cutoff = DateTime.UtcNow - Expiry;

        foreach (string file in EnumerateSafely(directory, "*.json", searchDirectories: false))
        {
            if (File.GetLastWriteTimeUtc(file) > cutoff)
                continue;

            if (TryDelete(() => File.Delete(file)))
                removed++;
        }

        if (removed > 0)
            TryLog(logger, $"Housekeeping removed {removed.ToString(CultureInfo.InvariantCulture)} leftover Service Bus topology file(s) older than {ExpiryFilter}.");
    }

    private static IReadOnlyList<string> EnumerateSafely(string root, string pattern, bool searchDirectories)
    {
        try
        {
            EnumerationOptions options = new()
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };

            return searchDirectories
                ? Directory.GetDirectories(root, pattern, options)
                : Directory.GetFiles(root, pattern, options);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private static bool TryDelete(Action delete)
    {
        try
        {
            delete();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A directory a running process still holds open comes round again tomorrow.
            return false;
        }
    }

    private static void TryLog(ScopedLogger? logger, string message)
    {
        try
        {
            logger?.LogInformation(message);
        }
        catch
        {
            // The sweep outlives the run it started from, so its logger may already be finished with.
        }
    }
}
