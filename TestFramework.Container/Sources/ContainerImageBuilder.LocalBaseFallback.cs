using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Core.Logging;

namespace TestFramework.Container.Sources;

public static partial class ContainerImageBuilder
{
    /// <summary>
    /// Builds from the locally cached base image once the SDK has already failed on the registry.
    /// </summary>
    /// <returns>The completed plan, or <see langword="null"/> when this route is not available either.</returns>
    /// <remarks>
    /// The second of the two chances this gets. The pre-flight check ahead of the publish catches the
    /// common case, but it cannot catch every one: the failure is intermittent, because the client
    /// races both address families and IPv4 sometimes wins, so a registry that answered a moment ago
    /// can still fail the build. That is what this is for.
    /// </remarks>
    private static async Task<ContainerSourcePlan?> TryRecoverFromLocalBaseAsync(
        ContainerSourcePlan plan,
        string identifier,
        string repository,
        string tag,
        string baseImage,
        ScopedLogger logger,
        CancellationToken cancellationToken)
    {
        if (plan.AssemblyFileName is not { Length: > 0 })
            return null;

        if (!await ImageExistsLocallyAsync(baseImage, cancellationToken).ConfigureAwait(false))
            return null;

        logger.LogWarning(
            $"The SDK could not reach the registry to resolve '{baseImage}' for '{identifier}', but that image is already in the local daemon. "
            + "Building it here instead, which needs no registry. A broken IPv6 path is the usual cause; see the package README.");

        return await BuildFromLocalBaseAsync(plan, identifier, repository, tag, baseImage, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the image from the base image already in the local daemon, contacting no registry.
    /// </summary>
    /// <param name="plan">The plan being carried out. Its entry assembly and project must be known.</param>
    /// <param name="identifier">The identifier the plan belongs to, for log output.</param>
    /// <param name="repository">The repository name for the image being produced.</param>
    /// <param name="tag">The tag for the image being produced.</param>
    /// <param name="baseImage">The base image, already known to be in the local daemon.</param>
    /// <param name="logger">The scoped logger.</param>
    /// <param name="cancellationToken">The cancellation token for the running setup.</param>
    /// <returns>The completed plan, or <see langword="null"/> when this route did not work either.</returns>
    /// <remarks>
    /// The SDK's publish always resolves the base image's manifest from the registry, even when that
    /// exact image is already in the local daemon. A plain <c>docker build</c> does not: it pulls
    /// only what is missing. So when the registry is out of reach but the base image is already here,
    /// the work is still perfectly doable -- publish to a directory, wrap it in a four-line
    /// Dockerfile, and let the daemon build it from the copy it already has.
    ///
    /// This is never silent. Whichever route reaches it says what it detected and what it did
    /// instead, and the plan comes back carrying a fallback reason, because a run that quietly takes
    /// a different route is worse than one that explains itself. Returning null rather than throwing
    /// keeps the original failure the one that gets reported: a caller that could not make this work
    /// has something better to say than "the workaround failed too".
    /// </remarks>
    internal static async Task<ContainerSourcePlan?> BuildFromLocalBaseAsync(
        ContainerSourcePlan plan,
        string identifier,
        string repository,
        string tag,
        string baseImage,
        ScopedLogger logger,
        CancellationToken cancellationToken)
    {
        string context = Path.Combine(Path.GetTempPath(), $"tf-localbase-{Guid.NewGuid().ToString("N")[..12]}");
        Directory.CreateDirectory(context);

        try
        {
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

            DotNetCliResult published = await DotNetCli.RunAsync(
                [
                    "publish",
                    plan.ProjectPath!,
                    "-c", plan.Configuration!,
                    "-f", plan.TargetFramework!,
                    "-o", context,
                ],
                Path.GetDirectoryName(plan.ProjectPath),
                cancellationToken).ConfigureAwait(false);

            if (!published.Succeeded)
            {
                logger.LogWarning($"The project for '{identifier}' could not be published on the host either, so there is nothing to wrap in an image.");
                return null;
            }

            await File.WriteAllTextAsync(
                Path.Combine(context, "Dockerfile"),
                DockerfileGenerator.WritePublishedOutputDockerfile(baseImage, plan.AssemblyFileName!),
                cancellationToken).ConfigureAwait(false);

            // The Dockerfile sits in the directory it copies, so it would otherwise end up inside the
            // image beside the application.
            await File.WriteAllTextAsync(Path.Combine(context, ".dockerignore"), "Dockerfile\n.dockerignore\n", cancellationToken).ConfigureAwait(false);

            string image = $"{repository}:{tag}";

            // No --pull: using what is already here is the entire point.
            ContainerDockerCommands.CommandResult built = await ContainerDockerCommands.RunAsync(
                $"build --label {ContainerLeftovers.BuildLabel}={ContainerLeftovers.BuildLabelValue} "
                    + $"--label {ContainerLeftovers.VendorLabel}={ContainerLeftovers.VendorLabelValue} -t {image} \"{context}\"",
                ContainerDockerCommands.BuildTimeout,
                cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();

            if (built.ExitCode != 0)
            {
                logger.LogWarning($"The local build for '{identifier}' failed: {built.StandardError.Trim()}");
                return null;
            }

            logger.LogInformation(
                $"'{identifier}' built image '{image}' from the local '{baseImage}' in {stopwatch.Elapsed:g}, without contacting a registry.");

            return plan with
            {
                Image = image,
                BuiltAtUtc = DateTimeOffset.UtcNow,
                FallbackReason = $"The registry was unreachable, so the image was built from the locally cached '{baseImage}' instead of by the SDK.",
            };
        }
        finally
        {
            try
            {
                Directory.Delete(context, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The sweep collects what a failed cleanup leaves behind.
            }
        }
    }
}
