using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Core.Exceptions;
using TestFramework.Core.Logging;

namespace TestFramework.Container.Sources;

/// <summary>
/// Builds an application image inside Docker, leaving no build output on the host.
/// </summary>
/// <remarks>
/// The build context is assembled in a temporary directory rather than pointed at the repository:
/// the generated Dockerfile, the offline package feed and a copy of the sources all live together
/// there, so nothing is written into the project being built and the context carries only what the
/// build needs.
/// </remarks>
public static class InContainerBuild
{
    private static readonly string[] ExcludedDirectories = ["bin", "obj", ".git", ".vs", "artifacts", "node_modules", "TestResults"];

    /// <summary>
    /// Builds the image a plan describes and returns the plan with the image filled in.
    /// </summary>
    /// <param name="plan">The plan to carry out.</param>
    /// <param name="facts">The project being built.</param>
    /// <param name="identifier">The identifier the plan belongs to.</param>
    /// <param name="logger">The scoped logger.</param>
    /// <param name="cancellationToken">The cancellation token for the running setup.</param>
    /// <exception cref="FrameworkConfigurationException">The context could not be assembled, or the build failed.</exception>
    public static async Task<ContainerSourcePlan> BuildAsync(
        ContainerSourcePlan plan,
        ProjectFacts facts,
        string identifier,
        ScopedLogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        ArgumentNullException.ThrowIfNull(logger);

        string contextRoot = Path.Combine(Path.GetTempPath(), $"tf-build-{Guid.NewGuid().ToString("N")[..12]}");
        string sourceRoot = plan.ContextDirectory ?? facts.ProjectDirectory;

        try
        {
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

            OfflineFeedResult feed = await OfflineFeed.CreateAsync(
                facts,
                plan.TargetFramework!,
                Path.Combine(contextRoot, DockerfileGenerator.PackagesDirectoryName),
                cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "'{0}' collected {1} package(s) from the host restore, so the build needs no feed credentials.",
                identifier,
                feed.Packages.Count);

            CopySources(sourceRoot, Path.Combine(contextRoot, DockerfileGenerator.SourceDirectoryName));

            string relativeProject = Path.GetRelativePath(sourceRoot, facts.ProjectPath);
            string dockerfile = DockerfileGenerator.WriteDockerfile(
                plan.SdkImage!,
                plan.RuntimeImage!,
                relativeProject,
                plan.AssemblyFileName!,
                plan.Configuration!,
                plan.TargetFramework!);

            await File.WriteAllTextAsync(Path.Combine(contextRoot, "Dockerfile"), dockerfile, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(contextRoot, ".dockerignore"), DockerfileGenerator.WriteDockerIgnore(), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(contextRoot, DockerfileGenerator.NuGetConfigFileName), DockerfileGenerator.WriteNuGetConfig(), cancellationToken).ConfigureAwait(false);

            // Written to the log because it is the first thing worth reading when a build fails.
            logger.LogInformation("'{0}' builds with:{1}{2}", identifier, Environment.NewLine, dockerfile);

            string image = $"{ContainerImageBuilder.ToRepositoryName(identifier)}:run-{Guid.NewGuid().ToString("N")[..8]}";
            await BuildImageAsync(contextRoot, image, identifier, cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();
            logger.LogInformation("'{0}' built image '{1}' in a container in {2}.", identifier, image, stopwatch.Elapsed);

            // Kept when the build fails: a context that is gone cannot be inspected, and that is
            // exactly when someone needs to look at it.
            DeleteContext(contextRoot, logger);

            return plan with { Image = image, BuiltAtUtc = DateTimeOffset.UtcNow };
        }
        catch
        {
            logger.LogWarning($"The build context for '{identifier}' was kept at '{contextRoot}'.");
            throw;
        }
    }

    /// <summary>
    /// Copies a source tree, skipping the directories a build produces.
    /// </summary>
    /// <param name="source">The directory to copy from.</param>
    /// <param name="destination">The directory to copy into.</param>
    /// <returns>The number of files copied.</returns>
    public static int CopySources(string source, string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        if (!Directory.Exists(source))
            throw new FrameworkConfigurationException($"The build context '{source}' does not exist.");

        Directory.CreateDirectory(destination);
        int copied = 0;

        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(directory);
            if (ExcludedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;

            copied += CopySources(directory, Path.Combine(destination, name));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
            copied++;
        }

        return copied;
    }

    private static async Task BuildImageAsync(string contextRoot, string image, string identifier, CancellationToken cancellationToken)
    {
        // The CLI is used rather than the client library because a failing build is only actionable
        // with the compiler and restore output, and the library reports a build failure as "the
        // image was not created".
        ContainerDockerCommands.CommandResult result = await ContainerDockerCommands
            .RunAsync($"build -t {image} -f \"{Path.Combine(contextRoot, "Dockerfile")}\" \"{contextRoot}\"", cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode == 0)
            return;

        throw new FrameworkConfigurationException(
            $"The container build for '{identifier}' failed.",
            [
                "The build output follows this message; the generated Dockerfile is in the run log above it.",
                $"The build context was kept at '{contextRoot}' so it can be inspected.",
                "A restore failure here means a package the host resolved was not copied into the feed.",
            ],
            [result.StandardError.Trim(), result.StandardOutput.Trim()]);
    }

    private static void DeleteContext(string contextRoot, ScopedLogger logger)
    {
        try
        {
            if (Directory.Exists(contextRoot))
                Directory.Delete(contextRoot, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The point of building in a container is to leave nothing behind; failing to clean up a
            // temporary directory is untidy, not a reason to fail the run.
            logger.LogWarning($"The build context '{contextRoot}' could not be removed: {exception.Message}");
        }
    }
}
