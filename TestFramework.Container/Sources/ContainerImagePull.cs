using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Core.Logging;

namespace TestFramework.Container.Sources;

/// <summary>
/// Gets an image onto this machine, including on a network where the daemon cannot pull it itself.
/// </summary>
/// <remarks>
/// A registry hostname resolves to both address families. Where the IPv6 path is broken -- a PPPoE
/// line behind a VPN is enough, and filtered ICMPv6 makes it silent -- the daemon's own pull fails
/// on the TLS handshake while everything else on the machine keeps working. That is not something a
/// test framework can fix by configuring its own HTTP client, because the daemon makes that
/// connection. It can, however, fetch the image over a path that works and hand it over.
/// </remarks>
public static class ContainerImagePull
{
    /// <summary>
    /// Ensures an image is in the local daemon, pulling it over IPv4 if the daemon cannot.
    /// </summary>
    /// <param name="image">The image reference.</param>
    /// <param name="logger">The scoped logger.</param>
    /// <param name="cancellationToken">The cancellation token for the running setup.</param>
    /// <returns><see langword="true"/> when the image is present afterwards.</returns>
    /// <remarks>
    /// Ordinary pulls are left alone: the daemon is asked first and this only does anything when that
    /// fails. Nothing here is silent -- the route taken is logged, because a run that quietly fetched
    /// its image a different way is worse than one that says so.
    /// </remarks>
    public static async Task<bool> EnsureAvailableAsync(string image, ScopedLogger logger, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        ArgumentNullException.ThrowIfNull(logger);

        if (await ContainerImageBuilder.ImageExistsLocallyAsync(image, cancellationToken).ConfigureAwait(false))
            return true;

        ContainerDockerCommands.CommandResult pulled = await ContainerDockerCommands
            .RunAsync($"pull {image}", ContainerDockerCommands.BuildTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (pulled.ExitCode == 0)
            return true;

        if (!LooksLikeATransportFailure(pulled))
        {
            // A missing tag or a login problem is not something a different route fixes, and trying
            // would only replace a clear error with a stranger one.
            logger.LogWarning($"'{image}' could not be pulled: {pulled.StandardError.Trim()}");
            return false;
        }

        return await FetchOverIPv4Async(image, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches the image over IPv4 and hands it to the daemon as an archive.
    /// </summary>
    /// <remarks>
    /// An archive rather than a registry served on loopback, because the daemon's <c>127.0.0.1</c> is
    /// not necessarily this machine's: Docker Desktop runs the engine in a virtual machine, so a
    /// listener here is simply not there as far as it is concerned. <c>docker load</c> is streamed
    /// through the CLI and crosses that boundary without anything being configured.
    /// </remarks>
    internal static async Task<bool> FetchOverIPv4Async(string image, ScopedLogger logger, CancellationToken cancellationToken)
    {
        string host = ContainerRegistryProbe.RegistryHostOf(image);
        if (string.Equals(host, ContainerRegistryProbe.DockerHubHost, StringComparison.OrdinalIgnoreCase))
        {
            // Docker Hub answers an anonymous pull only after a token exchange, which this does not
            // do. Saying so beats a 401 arriving as an unexplained fetch failure.
            logger.LogWarning(
                $"'{image}' comes from Docker Hub, and this route does not perform its token exchange. Pull it once on a working network.");
            return false;
        }

        logger.LogWarning(
            $"The daemon could not reach '{host}' to pull '{image}'. Fetching it over IPv4 from this process instead and loading it into the daemon. "
            + "A broken IPv6 path is the usual cause; see the package README.");

        string archive = Path.Combine(Path.GetTempPath(), $"tf-image-{Guid.NewGuid().ToString("N")[..12]}.tar");

        try
        {
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

            await RegistryImageFetcher.WriteArchiveAsync(
                host, RepositoryOf(image, host), ReferenceOf(image), image, archive, cancellationToken).ConfigureAwait(false);

            ContainerDockerCommands.CommandResult loaded = await ContainerDockerCommands
                .RunAsync($"load -i \"{archive}\"", ContainerDockerCommands.BuildTimeout, cancellationToken)
                .ConfigureAwait(false);

            stopwatch.Stop();

            if (loaded.ExitCode != 0)
            {
                logger.LogWarning($"'{image}' was fetched but could not be loaded: {loaded.StandardError.Trim()}");
                return false;
            }

            logger.LogInformation(
                $"'{image}' is now in the local daemon after {stopwatch.Elapsed:g}, fetched over IPv4 without the daemon reaching the registry.");

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning($"'{image}' could not be fetched over IPv4 either: {exception.Message}");
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(archive))
                    File.Delete(archive);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The sweep collects what a failed cleanup leaves behind.
            }
        }
    }

    /// <summary>
    /// Whether a failed pull looks like the network rather than the image being wrong.
    /// </summary>
    internal static bool LooksLikeATransportFailure(ContainerDockerCommands.CommandResult result)
    {
        string output = $"{result.StandardOutput}\n{result.StandardError}";

        return output.Contains("failed to do request", StringComparison.OrdinalIgnoreCase)
            || output.Contains("EOF", StringComparison.Ordinal)
            || output.Contains("TLS handshake", StringComparison.OrdinalIgnoreCase)
            || output.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
            || output.Contains("i/o timeout", StringComparison.OrdinalIgnoreCase)
            || output.Contains("no route to host", StringComparison.OrdinalIgnoreCase)
            || output.Contains("network is unreachable", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The repository part of an image reference, without the registry and without the tag.
    /// </summary>
    internal static string RepositoryOf(string image, string host)
    {
        string withoutHost = image.StartsWith($"{host}/", StringComparison.OrdinalIgnoreCase)
            ? image[(host.Length + 1)..]
            : image;

        int tag = withoutHost.LastIndexOf(':');
        int slash = withoutHost.LastIndexOf('/');

        return tag > slash ? withoutHost[..tag] : withoutHost;
    }

    /// <summary>
    /// The tag or digest of an image reference, defaulting to <c>latest</c>.
    /// </summary>
    internal static string ReferenceOf(string image)
    {
        int tag = image.LastIndexOf(':');
        int slash = image.LastIndexOf('/');

        return tag > slash ? image[(tag + 1)..] : "latest";
    }
}
