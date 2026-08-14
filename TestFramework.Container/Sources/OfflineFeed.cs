using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Sources;

/// <summary>
/// A package a restore resolved.
/// </summary>
/// <param name="Id">The package identifier.</param>
/// <param name="Version">The resolved version.</param>
public sealed record ResolvedPackage(string Id, string Version)
{
    /// <summary>
    /// The file name the package is cached under.
    /// </summary>
    public string FileName => $"{Id.ToLowerInvariant()}.{Version.ToLowerInvariant()}.nupkg";

    /// <summary>
    /// Returns a readable description of the package.
    /// </summary>
    public override string ToString() => $"{Id} {Version}";
}

/// <summary>
/// The outcome of building an offline feed.
/// </summary>
/// <param name="Directory">The directory holding the copied packages.</param>
/// <param name="Packages">The packages that were copied.</param>
/// <param name="Missing">Packages the cache did not hold, which the build would fail on.</param>
public sealed record OfflineFeedResult(string Directory, IReadOnlyList<ResolvedPackage> Packages, IReadOnlyList<ResolvedPackage> Missing);

/// <summary>
/// Builds a package feed out of what the host already restored.
/// </summary>
/// <remarks>
/// This exists so a container build needs no feed credentials. The host restores with the
/// configuration and credentials that already work; the exact set of packages that produced is
/// copied into the build context, and the build inside the container resolves from that and nothing
/// else. It cannot reach a private feed, and it cannot resolve a version the host did not.
/// </remarks>
public static class OfflineFeed
{
    /// <summary>
    /// Restores a project on the host and copies the resolved packages into a directory.
    /// </summary>
    /// <param name="facts">The project to restore.</param>
    /// <param name="targetFramework">The framework being built.</param>
    /// <param name="destination">The directory to fill.</param>
    /// <param name="cancellationToken">The cancellation token for the running setup.</param>
    /// <exception cref="FrameworkConfigurationException">The restore failed, or a resolved package is not in the cache.</exception>
    public static async Task<OfflineFeedResult> CreateAsync(
        ProjectFacts facts,
        string targetFramework,
        string destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFramework);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        DotNetCliResult restore = await DotNetCli.RunAsync(
            ["restore", facts.ProjectPath],
            facts.ProjectDirectory,
            cancellationToken).ConfigureAwait(false);

        if (!restore.Succeeded)
        {
            throw new FrameworkConfigurationException(
                $"'{facts.ProjectPath}' could not be restored on this machine, so no offline feed can be built from it.",
                ["The container build resolves only what the host resolved, so the host restore has to succeed first."],
                [restore.Describe()]);
        }

        IReadOnlyList<string> assetsFiles = FindAssetsFiles(facts);
        if (assetsFiles.Count == 0)
            throw new FrameworkConfigurationException($"No restore output was found for '{facts.ProjectPath}', so its packages cannot be collected.");

        HashSet<string> packageFolders = [];
        Dictionary<string, ResolvedPackage> packages = new(StringComparer.OrdinalIgnoreCase);

        foreach (string assetsFile in assetsFiles)
            ReadAssets(assetsFile, packages, packageFolders);

        Directory.CreateDirectory(destination);
        List<ResolvedPackage> copied = [];
        List<ResolvedPackage> missing = [];

        // Copied as the extracted folders the cache holds, not as .nupkg files. A container that is
        // handed a populated cache does not re-resolve; one handed a feed does, and would then ask
        // for packages this restore pruned as framework-provided.
        foreach (ResolvedPackage package in packages.Values.OrderBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase))
        {
            string? source = FindPackageDirectory(package, packageFolders);
            if (source is null)
            {
                missing.Add(package);
                continue;
            }

            CopyDirectory(source, Path.Combine(destination, package.Id.ToLowerInvariant(), package.Version.ToLowerInvariant()));
            copied.Add(package);
        }

        copied.AddRange(CopyTargetingPacks(targetFramework, packageFolders, destination));

        if (missing.Count > 0)
        {
            throw new FrameworkConfigurationException(
                $"{missing.Count} package(s) resolved by the restore are not in the local cache, so an offline build would fail.",
                [
                    "Run a full restore on this machine before the test, so every package is cached.",
                    "A package restored from a fallback folder rather than a feed can be absent from the cache.",
                ],
                [.. missing.Select(package => package.ToString())]);
        }

        return new OfflineFeedResult(destination, copied, missing);
    }

    private static IReadOnlyList<string> FindAssetsFiles(ProjectFacts facts)
    {
        // The entry project's assets normally carry the whole closure, but a referenced project can
        // resolve packages of its own, so each one is read and the results are merged.
        List<string> files = [];
        foreach (string project in new[] { facts.ProjectPath }.Concat(facts.ProjectReferences))
        {
            string candidate = Path.Combine(Path.GetDirectoryName(project)!, "obj", "project.assets.json");
            if (File.Exists(candidate))
                files.Add(candidate);
        }

        return files;
    }

    private static void ReadAssets(string assetsFile, Dictionary<string, ResolvedPackage> packages, HashSet<string> packageFolders)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(assetsFile));
        }
        catch (JsonException exception)
        {
            throw new FrameworkConfigurationException($"The restore output '{assetsFile}' could not be read.", null, null, exception);
        }

        if (root is not JsonObject assets)
            return;

        // The restore records where it put the packages, which is more reliable than assuming the
        // default cache location.
        if (assets["packageFolders"] is JsonObject folders)
        {
            foreach ((string folder, JsonNode? _) in folders)
                packageFolders.Add(folder);
        }

        if (assets["libraries"] is not JsonObject libraries)
            return;

        foreach ((string key, JsonNode? entry) in libraries)
        {
            if (entry is not JsonObject library
                || library["type"] is not JsonValue typeValue
                || !typeValue.TryGetValue(out string? type)
                || !string.Equals(type, "package", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] parts = key.Split('/', 2);
            if (parts.Length == 2)
                packages[key] = new ResolvedPackage(parts[0], parts[1]);
        }
    }

    /// <summary>
    /// The packs that describe a framework to the compiler, which are not part of a restore graph.
    /// </summary>
    private static readonly string[] TargetingPackIds = ["microsoft.netcore.app.ref", "microsoft.aspnetcore.app.ref"];

    private static IReadOnlyList<ResolvedPackage> CopyTargetingPacks(
        string targetFramework,
        IReadOnlyCollection<string> packageFolders,
        string destination)
    {
        // An SDK image bundles the targeting packs of its own generation only. Building an older
        // framework needs that generation's packs, and the host already downloaded them when it
        // restored the same project, so they travel with everything else.
        string major = targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase)
            ? targetFramework[3..].Split('.')[0]
            : string.Empty;

        if (major.Length == 0)
            return [];

        List<ResolvedPackage> copied = [];
        foreach (string packId in TargetingPackIds)
        {
            foreach (string folder in packageFolders)
            {
                string packRoot = Path.Combine(folder, packId);
                if (!Directory.Exists(packRoot))
                    continue;

                string? version = Directory
                    .EnumerateDirectories(packRoot, $"{major}.*", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .LastOrDefault();

                if (version is null)
                    continue;

                string name = Path.GetFileName(version);
                CopyDirectory(version, Path.Combine(destination, packId, name));
                copied.Add(new ResolvedPackage(packId, name));
                break;
            }
        }

        return copied;
    }

    private static string? FindPackageDirectory(ResolvedPackage package, IEnumerable<string> packageFolders)
    {
        foreach (string folder in packageFolders)
        {
            string candidate = Path.Combine(folder, package.Id.ToLowerInvariant(), package.Version.ToLowerInvariant());
            if (Directory.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
    }
}
