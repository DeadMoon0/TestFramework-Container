using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TestFramework.Container.Sources;

/// <summary>
/// Answers whether the registry an image comes from can be reached from this machine.
/// </summary>
/// <remarks>
/// This exists because the .NET SDK's container publish resolves the base image's manifest from a
/// registry on every build, even when that exact image is already in the local daemon. On a network
/// where the registry is unreachable, the SDK spends about two and a half minutes retrying before it
/// gives up -- and only then can anything else be tried. Asking first costs a few hundred
/// milliseconds when the network is healthy and turns those two and a half minutes into seconds when
/// it is not.
///
/// The check is a real HTTPS request rather than a ping or a TCP connect, because the failure this
/// was written for is a TLS one: the connection is made, and the handshake then dies on the
/// certificate chain because those packets exceed the real path MTU and the ICMP message that would
/// report it is filtered. A connect test passes on exactly the networks this needs to catch.
///
/// Any HTTP response at all counts as reachable, including 401, which is what a registry returns for
/// an unauthenticated <c>/v2/</c> request. The question is whether bytes flow, not whether this
/// process is allowed to pull.
///
/// The answer is a hint and never the last word: the failure is intermittent, because the client
/// races both address families and IPv4 sometimes wins. A probe that wrongly reports success only
/// means the SDK is tried first and the recovery happens afterwards, as it did before this existed.
/// </remarks>
internal static class ContainerRegistryProbe
{
    /// <summary>
    /// The registry an image reference with no registry in it comes from.
    /// </summary>
    public const string DockerHubHost = "registry-1.docker.io";

    /// <summary>
    /// How long the probe waits before calling the registry unreachable.
    /// </summary>
    /// <remarks>
    /// Only ever paid in full on a broken network: a healthy registry answers in well under a second,
    /// so the budget can be generous enough that a slow-but-working link is not mistaken for a broken
    /// one. The alternative it is being weighed against costs about two and a half minutes.
    /// </remarks>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    private static readonly ConcurrentDictionary<string, bool> Answers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the registry holding <paramref name="imageReference"/> answered.
    /// </summary>
    /// <param name="imageReference">The image whose registry is in question.</param>
    /// <param name="cancellationToken">The cancellation token for the running setup.</param>
    /// <remarks>
    /// Answered once per host per process. A run that builds several images asks about the same
    /// registry every time, and the second answer would cost another eight seconds on the network
    /// this was written for.
    /// </remarks>
    public static async Task<bool> IsReachableAsync(string imageReference, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);

        string host = RegistryHostOf(imageReference);
        if (Answers.TryGetValue(host, out bool remembered))
            return remembered;

        bool reachable = await ProbeAsync(host, cancellationToken).ConfigureAwait(false);
        Answers[host] = reachable;
        return reachable;
    }

    /// <summary>
    /// Forgets what was answered, so the next question is asked again.
    /// </summary>
    internal static void Forget() => Answers.Clear();

    /// <summary>
    /// Returns the registry host an image reference names.
    /// </summary>
    /// <param name="imageReference">The image reference.</param>
    /// <remarks>
    /// The first segment is a registry when it looks like one -- it carries a dot, a port, or is
    /// localhost. Everything else is a Docker Hub repository, including a bare <c>redis:7</c>, which
    /// names no registry at all.
    /// </remarks>
    internal static string RegistryHostOf(string imageReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);

        int slash = imageReference.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
            return DockerHubHost;

        string first = imageReference[..slash];

        return first.Contains('.', StringComparison.Ordinal)
            || first.Contains(':', StringComparison.Ordinal)
            || first.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                ? first
                : DockerHubHost;
    }

    private static async Task<bool> ProbeAsync(string host, CancellationToken cancellationToken)
    {
        using CancellationTokenSource expiry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        expiry.CancelAfter(Timeout);

        // A handler of its own rather than a shared client: this runs once per registry per process,
        // and a redirect chase or a connection kept alive afterwards would both be waste.
        using HttpClientHandler handler = new() { AllowAutoRedirect = false };
        using HttpClient client = new(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, $"https://{host}/v2/");
            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, expiry.Token)
                .ConfigureAwait(false);

            // The status does not matter. A 401 is the normal answer here, and it proves the
            // handshake completed and the response came back, which is the whole question.
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            return false;
        }
    }
}
