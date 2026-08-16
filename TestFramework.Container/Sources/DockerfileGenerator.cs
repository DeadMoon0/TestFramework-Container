using System;
using System.Collections.Generic;
using System.Linq;

namespace TestFramework.Container.Sources;

/// <summary>
/// Writes the files that describe an in-container build.
/// </summary>
/// <remarks>
/// Generated as text rather than assembled through an API, because the result has to be readable:
/// when a build fails, the Dockerfile is the first thing worth looking at, and it is written into
/// the run log for exactly that reason.
/// </remarks>
public static class DockerfileGenerator
{
    /// <summary>
    /// The directory inside the build context holding the copied sources.
    /// </summary>
    public const string SourceDirectoryName = "src";

    /// <summary>
    /// The directory inside the build context holding the offline packages.
    /// </summary>
    public const string PackagesDirectoryName = "packages";

    /// <summary>
    /// The file name of the generated NuGet configuration.
    /// </summary>
    public const string NuGetConfigFileName = "NuGet.Offline.config";

    /// <summary>
    /// Writes the Dockerfile for a two-stage build that restores offline.
    /// </summary>
    /// <param name="sdkImage">The image the build stage runs on.</param>
    /// <param name="runtimeImage">The image the application ends up on.</param>
    /// <param name="relativeProjectPath">The project path relative to the copied source root, with forward slashes.</param>
    /// <param name="assemblyFileName">The entry assembly file name.</param>
    /// <param name="configuration">The build configuration.</param>
    /// <param name="targetFramework">The framework to build.</param>
    public static string WriteDockerfile(
        string sdkImage,
        string runtimeImage,
        string relativeProjectPath,
        string assemblyFileName,
        string configuration,
        string targetFramework)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sdkImage);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeImage);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeProjectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFramework);

        string project = ToContainerPath(relativeProjectPath);

        // NuGetAudit is disabled because it reaches out for vulnerability data, which an offline
        // restore cannot do and which would otherwise turn into a warning, or an error in a project
        // that treats warnings as errors.
        //
        // UseAppHost is disabled because the native launcher is a per-platform package: a feed built
        // from a Windows restore holds the win-x64 one, and the Linux build would ask for a package
        // that was never downloaded. The container starts the application with 'dotnet app.dll', so
        // the launcher is not needed at all.
        const string offlineProperties = "-p:NuGetAudit=false -p:UseAppHost=false";

        string[] lines =
        [
            $"FROM {sdkImage} AS build",
            // The packages are handed over as an already-populated cache rather than as a feed to
            // resolve from: a restore that re-resolves would ask for packages the host's restore
            // pruned, and no feed built from the result can satisfy that.
            "ENV NUGET_PACKAGES=/packages",
            "WORKDIR /src",
            $"COPY {PackagesDirectoryName}/ /packages/",
            $"COPY {NuGetConfigFileName} ./NuGet.config",
            $"COPY {SourceDirectoryName}/ ./",
            // Restore is narrowed to the one framework being built: a multi-targeted project would
            // otherwise restore every framework it declares, including ones this SDK cannot target.
            $"RUN dotnet restore \"{project}\" --configfile ./NuGet.config -p:TargetFramework={targetFramework} {offlineProperties}",
            $"RUN dotnet publish \"{project}\" -c {configuration} -f {targetFramework} -o /app --no-restore {offlineProperties}",
            string.Empty,
            $"FROM {runtimeImage}",
            "WORKDIR /app",
            "COPY --from=build /app .",
            $"ENTRYPOINT [\"dotnet\", \"{assemblyFileName}\"]",
        ];

        // Written with newline endings whatever the host uses, because the file is read inside a
        // Linux container.
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>
    /// Writes the Dockerfile that wraps an already published output in a runtime image.
    /// </summary>
    /// <param name="runtimeImage">The image the application ends up on.</param>
    /// <param name="assemblyFileName">The entry assembly file name.</param>
    /// <remarks>
    /// Deliberately the same shape the SDK's own container publish produces -- the output at
    /// <c>/app</c>, the working directory there, and the application started with
    /// <c>dotnet app.dll</c> -- because this stands in for that publish when the registry cannot be
    /// reached, and an image that behaved differently would turn a network problem into a test that
    /// fails for a second, unrelated reason.
    ///
    /// There is no build stage. The output was published on the host and is copied in as it is, so
    /// nothing here needs the SDK image, and the runtime image is the one the daemon already has.
    /// </remarks>
    public static string WritePublishedOutputDockerfile(string runtimeImage, string assemblyFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeImage);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyFileName);

        string[] lines =
        [
            $"FROM {runtimeImage}",
            "WORKDIR /app",
            "COPY . .",
            $"ENTRYPOINT [\"dotnet\", \"{assemblyFileName}\"]",
        ];

        return string.Join("\n", lines) + "\n";
    }

    /// <summary>
    /// Writes a NuGet configuration with no sources at all.
    /// </summary>
    /// <remarks>
    /// Clearing the sources is the point: the build cannot reach a feed, so it needs no credentials,
    /// and it cannot resolve anything the host did not already resolve. Everything it needs is in
    /// the cache the context carries.
    /// </remarks>
    public static string WriteNuGetConfig()
        => string.Join("\n",
        [
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>",
            "<configuration>",
            "  <packageSources>",
            "    <clear />",
            "  </packageSources>",
            "</configuration>",
        ]) + "\n";

    /// <summary>
    /// Writes the ignore file that keeps build output out of the context.
    /// </summary>
    /// <param name="additionalPatterns">Extra patterns to exclude.</param>
    public static string WriteDockerIgnore(IEnumerable<string>? additionalPatterns = null)
    {
        IEnumerable<string> patterns =
        [
            "**/bin/",
            "**/obj/",
            "**/.git/",
            "**/.vs/",
            "**/artifacts/",
            "**/node_modules/",
            "**/TestResults/",
            .. additionalPatterns ?? [],
        ];

        return string.Join("\n", patterns) + "\n";
    }

    /// <summary>
    /// Converts a host-relative path into the forward-slash form a container understands.
    /// </summary>
    /// <param name="path">The path to convert.</param>
    public static string ToContainerPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return path.Replace('\\', '/').TrimStart('/');
    }
}
