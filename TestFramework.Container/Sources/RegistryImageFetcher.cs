using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Sources;

/// <summary>
/// Downloads an image from a registry and writes it as an archive the daemon can load.
/// </summary>
/// <remarks>
/// <para>
/// This exists because on some networks the Docker daemon cannot pull at all. A registry hostname
/// resolves to both address families; the daemon picks IPv6, TCP connects because the first packets
/// are small, and the TLS handshake then dies because the certificate chain exceeds the real path
/// MTU while the ICMPv6 message that would report it is filtered. Measured on the machine this was
/// written for: IPv4 answers in under 100 ms three times out of three, IPv6 fails two times out of
/// three. Everything else on that machine works, which is what makes it so hard to read.
/// </para>
/// <para>
/// The daemon makes its own connections, so no amount of configuring this process's HTTP client
/// changes what <c>docker pull</c> does. What this process can do is fetch the image over a path
/// that works and hand the result over as a file. <c>docker load</c> streams the archive through the
/// CLI, so it works whether the daemon runs on this machine or, as with Docker Desktop, inside a
/// virtual machine of its own -- which is what rules out the other obvious idea, serving a registry
/// on loopback: the daemon's <c>127.0.0.1</c> is not this machine's.
/// </para>
/// <para>
/// Every connection made here is pinned to IPv4. Anonymous registries only: no token exchange is
/// performed, so Docker Hub and private registries still need a working daemon connection.
/// </para>
/// </remarks>
internal static class RegistryImageFetcher
{
    private const string DockerManifestList = "application/vnd.docker.distribution.manifest.list.v2+json";
    private const string DockerManifest = "application/vnd.docker.distribution.manifest.v2+json";
    private const string OciIndex = "application/vnd.oci.image.index.v1+json";
    private const string OciManifest = "application/vnd.oci.image.manifest.v1+json";

    /// <summary>
    /// Downloads an image and writes it as a docker archive.
    /// </summary>
    /// <param name="host">The registry host.</param>
    /// <param name="repository">The repository, for example <c>dotnet/aspnet</c>.</param>
    /// <param name="reference">The tag or digest.</param>
    /// <param name="repoTag">The name the loaded image is given.</param>
    /// <param name="archivePath">Where to write the archive.</param>
    /// <param name="cancellationToken">The cancellation token for the running fetch.</param>
    public static async Task WriteArchiveAsync(
        string host,
        string repository,
        string reference,
        string repoTag,
        string archivePath,
        CancellationToken cancellationToken)
    {
        using HttpClient client = CreateIPv4Client();

        string manifestJson = await GetStringAsync(client, $"https://{host}/v2/{repository}/manifests/{reference}", cancellationToken).ConfigureAwait(false);
        using JsonDocument manifestDocument = JsonDocument.Parse(manifestJson);
        JsonElement root = manifestDocument.RootElement;

        // A multi-platform tag answers with a list, and picking from it is this code's job rather
        // than the registry's: an index names every platform it was built for and says nothing about
        // which one is wanted here.
        if (root.TryGetProperty("manifests", out JsonElement entries))
        {
            string digest = SelectPlatformDigest(entries, repository, reference);
            manifestJson = await GetStringAsync(client, $"https://{host}/v2/{repository}/manifests/{digest}", cancellationToken).ConfigureAwait(false);
        }

        using JsonDocument imageDocument = JsonDocument.Parse(manifestJson);
        JsonElement image = imageDocument.RootElement;

        string configDigest = image.GetProperty("config").GetProperty("digest").GetString()
            ?? throw new FrameworkStateException($"The manifest for '{repository}:{reference}' names no configuration blob.");

        byte[] config = await GetBytesAsync(client, $"https://{host}/v2/{repository}/blobs/{configDigest}", cancellationToken).ConfigureAwait(false);

        string workingDirectory = Path.Combine(Path.GetTempPath(), $"tf-pull-{Guid.NewGuid().ToString("N")[..12]}");
        Directory.CreateDirectory(workingDirectory);

        try
        {
            List<string> layerEntries = [];
            int index = 0;

            foreach (JsonElement layer in image.GetProperty("layers").EnumerateArray())
            {
                string digest = layer.GetProperty("digest").GetString()!;
                string mediaType = layer.TryGetProperty("mediaType", out JsonElement type) ? type.GetString() ?? string.Empty : string.Empty;

                if (mediaType.Contains("zstd", StringComparison.OrdinalIgnoreCase))
                {
                    throw new FrameworkConfigurationException(
                        $"'{repository}:{reference}' ships zstd-compressed layers, which this fallback cannot unpack.",
                        [$"Pull it once on a working network: docker pull {repoTag}."]);
                }

                string layerDirectory = Path.Combine(workingDirectory, index.ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(layerDirectory);

                // The archive format wants uncompressed layers, and a registry serves them gzipped.
                await using (Stream download = await GetStreamAsync(client, $"https://{host}/v2/{repository}/blobs/{digest}", cancellationToken).ConfigureAwait(false))
                await using (GZipStream decompressed = new(download, CompressionMode.Decompress))
                await using (FileStream target = File.Create(Path.Combine(layerDirectory, "layer.tar")))
                {
                    await decompressed.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                }

                layerEntries.Add($"{index}/layer.tar");
                index++;
            }

            string configFileName = $"{configDigest.Replace("sha256:", string.Empty, StringComparison.Ordinal)}.json";
            await File.WriteAllBytesAsync(Path.Combine(workingDirectory, configFileName), config, cancellationToken).ConfigureAwait(false);

            await File.WriteAllTextAsync(
                Path.Combine(workingDirectory, "manifest.json"),
                JsonSerializer.Serialize(new[]
                {
                    new ArchiveManifestEntry(configFileName, [repoTag], [.. layerEntries]),
                }),
                cancellationToken).ConfigureAwait(false);

            if (File.Exists(archivePath))
                File.Delete(archivePath);

            TarFile.CreateFromDirectory(workingDirectory, archivePath, includeBaseDirectory: false);
        }
        finally
        {
            try
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The sweep collects what a failed cleanup leaves behind.
            }
        }
    }

    /// <summary>
    /// Picks the manifest for the platform this machine runs.
    /// </summary>
    private static string SelectPlatformDigest(JsonElement entries, string repository, string reference)
    {
        string architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "amd64",
            _ => "amd64",
        };

        foreach (JsonElement entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("platform", out JsonElement platform))
                continue;

            // Linux only: every base image this framework selects is a Linux image, and the daemon is
            // checked for Linux containers before any of this runs.
            if (platform.TryGetProperty("os", out JsonElement os)
                && string.Equals(os.GetString(), "linux", StringComparison.OrdinalIgnoreCase)
                && platform.TryGetProperty("architecture", out JsonElement arch)
                && string.Equals(arch.GetString(), architecture, StringComparison.OrdinalIgnoreCase))
            {
                return entry.GetProperty("digest").GetString()!;
            }
        }

        throw new FrameworkConfigurationException(
            $"'{repository}:{reference}' publishes no linux/{architecture} image.",
            ["Name an image that does, or run the daemon on a matching platform."]);
    }

    private static HttpClient CreateIPv4Client()
    {
        SocketsHttpHandler handler = new()
        {
            // The one line this whole class exists for. Without it the request leaves over the same
            // broken path the daemon would have used.
            ConnectCallback = static async (context, cancellationToken) =>
            {
                Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
            AutomaticDecompression = DecompressionMethods.None,
        };

        HttpClient client = new(handler) { Timeout = TimeSpan.FromMinutes(15) };
        foreach (string mediaType in new[] { DockerManifestList, DockerManifest, OciIndex, OciManifest })
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", mediaType);

        return client;
    }

    private static async Task<string> GetStringAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> GetBytesAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Stream> GetStreamAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record ArchiveManifestEntry(string Config, string[] RepoTags, string[] Layers);
}
