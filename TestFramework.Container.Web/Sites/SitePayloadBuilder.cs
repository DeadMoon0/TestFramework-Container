using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Container.Sources;
using TestFramework.Core.Exceptions;
using TestFramework.Core.Logging;

namespace TestFramework.Container.Web.Sites;

/// <summary>
/// Carries out a site plan: runs the build it states and verifies the payload it promises.
/// </summary>
/// <remarks>
/// Image and directory plans pass through untouched. Builds run one at a time in the environment's
/// planning phase -- npm shares one package cache, and a wrong answer from a concurrent build is a
/// wrong site, not a slow one.
/// </remarks>
public static class SitePayloadBuilder
{
    /// <summary>
    /// Runs the build a plan states, if any, and returns the plan with the payload in place.
    /// </summary>
    /// <param name="source">The declared source the plan came from.</param>
    /// <param name="plan">The stated plan.</param>
    /// <param name="identifier">The site identifier, named in logs and errors.</param>
    /// <param name="logger">The logger build output is reported to.</param>
    /// <param name="cancellationToken">The cancellation token for the running build.</param>
    /// <exception cref="FrameworkConfigurationException">The build failed or produced no payload.</exception>
    public static async Task<SiteSourcePlan> BuildAsync(
        SiteSource source,
        SiteSourcePlan plan,
        string identifier,
        ScopedLogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        ArgumentNullException.ThrowIfNull(logger);

        switch (source)
        {
            case NpmProjectSiteSource npm when npm.Strategy == SiteBuildStrategy.HostBuild:
                return await BuildNpmOnHostAsync(npm, plan, identifier, logger, cancellationToken).ConfigureAwait(false);

            case NpmProjectSiteSource npm:
                return await BuildInContainerAsync(
                    plan,
                    npm.ProjectDirectory,
                    npm.BuildImage,
                    $"npm ci && npm run {npm.BuildScript}",
                    npm.BuildTimeout,
                    identifier,
                    logger,
                    cancellationToken).ConfigureAwait(false);

            case ContainerBuildSiteSource build:
                return await BuildInContainerAsync(
                    plan,
                    build.ProjectDirectory,
                    build.BuildImage,
                    string.Join(' ', build.BuildCommand.Select(QuoteForShell)),
                    build.BuildTimeout,
                    identifier,
                    logger,
                    cancellationToken).ConfigureAwait(false);

            default:
                return plan;
        }
    }

    private static async Task<SiteSourcePlan> BuildNpmOnHostAsync(
        NpmProjectSiteSource source,
        SiteSourcePlan plan,
        string identifier,
        ScopedLogger logger,
        CancellationToken cancellationToken)
    {
        if (source.InstallWhenMissing && !Directory.Exists(Path.Combine(source.ProjectDirectory, "node_modules")))
        {
            logger.LogInformation("Site '{0}' installs packages: npm ci in '{1}'.", identifier, source.ProjectDirectory);
            NpmCliResult install = await NpmCli.RunAsync(["ci"], source.ProjectDirectory, source.BuildTimeout, cancellationToken).ConfigureAwait(false);
            if (!install.Succeeded)
                throw BuildFailed(identifier, source.ProjectDirectory, install.Describe());
        }

        logger.LogInformation("Site '{0}' builds: npm run {1} in '{2}'.", identifier, source.BuildScript, source.ProjectDirectory);
        Stopwatch stopwatch = Stopwatch.StartNew();
        NpmCliResult build = await NpmCli.RunAsync(["run", source.BuildScript], source.ProjectDirectory, source.BuildTimeout, cancellationToken).ConfigureAwait(false);
        if (!build.Succeeded)
            throw BuildFailed(identifier, source.ProjectDirectory, build.Describe());

        logger.LogInformation("Site '{0}' built in {1:0.0}s.", identifier, stopwatch.Elapsed.TotalSeconds);

        EnsureBuiltPayload(plan.DistDirectory!, identifier);
        return plan with { BuiltAtUtc = File.GetLastWriteTimeUtc(Path.Combine(plan.DistDirectory!, "index.html")) };
    }

    private static async Task<SiteSourcePlan> BuildInContainerAsync(
        SiteSourcePlan plan,
        string projectDirectory,
        string buildImage,
        string shellCommand,
        TimeSpan timeout,
        string identifier,
        ScopedLogger logger,
        CancellationToken cancellationToken)
    {
        string relativeDist = Path.GetRelativePath(projectDirectory, plan.DistDirectory!);
        if (relativeDist.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativeDist))
        {
            throw new FrameworkConfigurationException(
                $"Site '{identifier}' declares the output directory '{plan.DistDirectory}', which lies outside the project '{projectDirectory}'. A container build only sees the copied project.",
                ["Declare a dist path inside the project directory."]);
        }

        // The sources are copied rather than mounted: the host's node_modules must not travel (its
        // binaries are platform-specific), and the build must not write into the project's own tree.
        string contextRoot = Path.Combine(Path.GetTempPath(), $"tf-site-{Guid.NewGuid():N}"[..20]);
        Directory.CreateDirectory(contextRoot);
        int copied = InContainerBuild.CopySources(projectDirectory, contextRoot);
        logger.LogInformation("Site '{0}' copied {1} source file(s) to '{2}'.", identifier, copied, contextRoot);
        logger.LogInformation("Site '{0}' builds in '{1}': {2}", identifier, buildImage, shellCommand);

        // The container is named so an abandoned build can be reached: killing the CLI client stops
        // nothing daemon-side, and '--rm' removes a container only when it exits. Without the name, a
        // wedged 'npm ci' - the exact wedge the timeout exists for - kept running forever.
        string containerName = $"tf-sitebuild-{Guid.NewGuid():N}"[..24];
        string arguments = $"run --rm --name {containerName} --label {ContainerLeftovers.BuildLabel}={ContainerLeftovers.BuildLabelValue} -v \"{contextRoot}:/src\" -w /src {buildImage} sh -lc \"{shellCommand.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

        Stopwatch stopwatch = Stopwatch.StartNew();
        ContainerDockerCommands.CommandResult result;
        try
        {
            result = await ContainerDockerCommands.RunAsync(arguments, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await RemoveAbandonedBuildContainerAsync(containerName).ConfigureAwait(false);
            throw;
        }

        if (result.ExitCode == -1)
        {
            // -1 is the runner's own verdict: the client was killed on timeout, so the daemon-side
            // container is still running the wedged build and has to be stopped by name.
            await RemoveAbandonedBuildContainerAsync(containerName).ConfigureAwait(false);
        }

        if (result.ExitCode != 0)
        {
            // The context is kept on failure: it holds the exact sources the build saw.
            throw new FrameworkConfigurationException(
                $"The build of site '{identifier}' failed inside '{buildImage}'. {Trimmed(result.StandardError)}",
                [
                    $"The copied sources were kept at '{contextRoot}' for diagnosis.",
                    "The build container needs registry access to install packages; a .npmrc in the project travels with the sources.",
                ]);
        }

        logger.LogInformation("Site '{0}' built in {1:0.0}s.", identifier, stopwatch.Elapsed.TotalSeconds);

        string builtDist = Path.Combine(contextRoot, relativeDist);
        EnsureBuiltPayload(builtDist, identifier);

        return plan with
        {
            DistDirectory = builtDist,
            TemporaryRoot = contextRoot,
            BuiltAtUtc = File.GetLastWriteTimeUtc(Path.Combine(builtDist, "index.html")),
        };
    }

    private static async Task RemoveAbandonedBuildContainerAsync(string containerName)
    {
        try
        {
            // Best effort on a token of its own: this runs precisely when the caller's token is spent.
            await ContainerDockerCommands.RunAsync($"rm -f {containerName}", TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // A leftover build container is litter, never a reason to hide the failure that made it.
        }
    }

    private static void EnsureBuiltPayload(string distDirectory, string identifier)
    {
        if (!Directory.Exists(distDirectory) || !File.Exists(Path.Combine(distDirectory, "index.html")))
        {
            throw new FrameworkConfigurationException(
                $"The build of site '{identifier}' reported success, but '{distDirectory}' holds no index.html.",
                ["Declare the directory the build actually writes with WithDistPath(\"...\")."]);
        }
    }

    private static FrameworkConfigurationException BuildFailed(string identifier, string projectDirectory, string details)
        => new(
            $"The build of site '{identifier}' failed. {details}",
            [
                $"Run 'npm ci && npm run build' in '{projectDirectory}' to see the full output.",
                "node and npm must be on the PATH of the test host, or the source can build inside a container with BuiltInContainer().",
            ]);

    private static string QuoteForShell(string argument)
        => argument.Contains(' ', StringComparison.Ordinal) || argument.Contains('"', StringComparison.Ordinal)
            ? $"'{argument.Replace("'", "'\\''", StringComparison.Ordinal)}'"
            : argument;

    private static string Trimmed(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length <= 2000 ? trimmed : trimmed[^2000..];
    }
}
