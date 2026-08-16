using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Core.Exceptions;
using TestFramework.Core.Logging;

namespace TestFramework.Container.Sources;

/// <summary>
/// Carries out a source plan, producing either an image to run or a directory to ship.
/// </summary>
/// <remarks>
/// The same code path runs on a Windows and a Linux host. What differs between them -- how the
/// Docker client is reached, whether a <c>docker</c> executable exists, and whether the daemon is
/// serving Linux or Windows containers -- is checked here rather than left to fail somewhere deeper.
/// </remarks>
public static class ContainerImageBuilder
{
    private static readonly ConcurrentDictionary<string, ContainerSourcePlan> BuiltImages = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> BuildGates = new(StringComparer.Ordinal);

    /// <summary>
    /// How long the SDK publish may run when the base image is already in the local daemon.
    /// </summary>
    /// <remarks>
    /// Deliberately impatient, and only used when there is somewhere to go: a build that stops making
    /// progress here costs a short detour rather than the run, so waiting the full budget out buys
    /// nothing. Long enough that a genuine cold restore and compile still finishes.
    /// </remarks>
    internal static readonly TimeSpan SdkPublishTimeoutWithLocalFallback = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Builds what the plan describes and returns the plan with the result filled in.
    /// </summary>
    /// <param name="plan">The plan to carry out.</param>
    /// <param name="identifier">The identifier the plan belongs to, for log and error output.</param>
    /// <param name="logger">The scoped logger.</param>
    /// <param name="cancellationToken">The cancellation token for the running setup.</param>
    /// <exception cref="FrameworkConfigurationException">The plan cannot be carried out on this machine.</exception>
    public static async Task<ContainerSourcePlan> BuildAsync(
        ContainerSourcePlan plan,
        string identifier,
        ScopedLogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        ArgumentNullException.ThrowIfNull(logger);

        // Stated before anything happens, so a failure is read against what was going to be done.
        foreach (string line in plan.ToLogLines(identifier))
            logger.LogInformation(line);

        // Shipping something other than the project's own output is the difference between testing this
        // build and testing whatever a test host happened to copy, so it does not hide in the plan.
        if (plan.FallbackReason is { } fallbackReason)
            logger.LogWarning($"'{identifier}' is not shipping its project's own build output. {fallbackReason}");

        if (plan.Kind != ContainerSourceKind.Project)
            return plan;

        if (plan.Strategy == ContainerBuildStrategy.HostPublish)
            return await PublishToDirectoryAsync(plan, identifier, logger, cancellationToken).ConfigureAwait(false);

        return await BuildImageOnceAsync(plan, identifier, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the image for a plan, or hands back the one an earlier environment in this run built.
    /// </summary>
    /// <remarks>
    /// An image is decided entirely by its inputs -- the project, the configuration, the framework,
    /// the base image and how it is built -- so two environments describing the same inputs would
    /// produce the same image twice. They used to: every environment ran its own publish and its own
    /// image build, and threw the result away at teardown. A suite of a dozen chapters against one
    /// unchanged application therefore paid for a dozen identical builds, which on a normal machine
    /// is the difference between a run measured in minutes and one measured in tens of them.
    ///
    /// This does not weaken the isolation between runs. An image is read only: every container still
    /// gets its own writable layer, its own settings file, its own network and its own data. What is
    /// shared is the build output, which is the one thing that cannot differ between two environments
    /// that asked for the same thing.
    ///
    /// The gate is per identity rather than global, so two different applications still build at the
    /// same time while two environments wanting the same application do not race each other into
    /// running two publishes over one project directory.
    /// </remarks>
    private static async Task<ContainerSourcePlan> BuildImageOnceAsync(
        ContainerSourcePlan plan,
        string identifier,
        ScopedLogger logger,
        CancellationToken cancellationToken)
    {
        string identity = BuildIdentityOf(plan);
        SemaphoreSlim gate = BuildGates.GetOrAdd(identity, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await TryReuseAsync(identity, plan, identifier, logger, cancellationToken).ConfigureAwait(false) is { } reused)
                return reused;

            ContainerSourcePlan built = plan.Strategy switch
            {
                ContainerBuildStrategy.SdkContainerPublish => await PublishImageAsync(plan, identifier, logger, cancellationToken).ConfigureAwait(false),
                ContainerBuildStrategy.InContainer => await BuildInContainerAsync(plan, identifier, logger, cancellationToken).ConfigureAwait(false),
                _ => throw new FrameworkConfigurationException($"The build strategy '{plan.Strategy}' is not supported."),
            };

            if (built.Image is { Length: > 0 })
                BuiltImages[identity] = built;

            return built;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<ContainerSourcePlan?> TryReuseAsync(
        string identity,
        ContainerSourcePlan plan,
        string identifier,
        ScopedLogger logger,
        CancellationToken cancellationToken)
    {
        if (!BuiltImages.TryGetValue(identity, out ContainerSourcePlan? earlier) || earlier.Image is not { Length: > 0 } image)
            return null;

        // Asked rather than assumed: a developer clearing images between chapters, or a sweep on a
        // shared machine, would otherwise leave this handing out the name of an image that is gone,
        // and the failure would surface as a container that will not start.
        if (!await ImageExistsLocallyAsync(image, cancellationToken).ConfigureAwait(false))
        {
            BuiltImages.TryRemove(identity, out _);
            return null;
        }

        logger.LogInformation(
            "'{0}' is reusing image '{1}', already built in this run from the same project, configuration, framework and base image.",
            identifier,
            image);

        return plan with { Image = image, BuiltAtUtc = earlier.BuiltAtUtc, FallbackReason = earlier.FallbackReason };
    }

    /// <summary>
    /// Forgets which images this run built, so the next build produces a fresh one.
    /// </summary>
    internal static void ForgetBuiltImages() => BuiltImages.Clear();

    /// <summary>
    /// The inputs that fully decide what an image built from this plan contains.
    /// </summary>
    private static string BuildIdentityOf(ContainerSourcePlan plan)
        => string.Join(
            '|',
            plan.Strategy,
            plan.ProjectPath,
            plan.Configuration,
            plan.TargetFramework,
            plan.RuntimeImage,
            plan.SdkImage,
            plan.ContextDirectory);

    /// <summary>
    /// Removes an image this builder produced, unless later environments in this run still want it.
    /// </summary>
    /// <param name="image">The image reference.</param>
    /// <param name="cancellationToken">The cancellation token for the running teardown.</param>
    /// <remarks>
    /// A component tears its own environment down without knowing whether anything else in the run
    /// shares the image, so the decision is made here rather than there -- which also means a caller
    /// compiled against an earlier version keeps working unchanged. Removing an image that a later
    /// chapter is about to reuse would not just cost the rebuild; it would hand that chapter a name
    /// that no longer resolves.
    ///
    /// What is kept is not leaked. Images this framework builds carry its labels, and the leftover
    /// sweep reclaims them once they are a day old, which is the same mechanism that already covers
    /// a killed test host.
    /// </remarks>
    public static async Task RemoveImageAsync(string image, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);

        foreach (ContainerSourcePlan built in BuiltImages.Values)
        {
            if (string.Equals(built.Image, image, StringComparison.Ordinal))
                return;
        }

        try
        {
            await ContainerDockerCommands.RunAsync($"image rm -f {image}", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Teardown must not fail a run over a leftover image.
        }
    }

    private static async Task<ContainerSourcePlan> PublishImageAsync(
        ContainerSourcePlan plan,
        string identifier,
        ScopedLogger logger,
        CancellationToken cancellationToken)
    {
        await EnsureDockerCliAsync(cancellationToken).ConfigureAwait(false);
        await EnsureLinuxContainersAsync(cancellationToken).ConfigureAwait(false);

        string repository = ToRepositoryName(identifier);
        string tag = $"run-{Guid.NewGuid().ToString("N")[..8]}";

        // Asked before the SDK is started rather than after it has failed. The publish below resolves
        // the base image's manifest from a registry every time, even when that image is already here,
        // so on an unreachable registry it burns about two and a half minutes and then fails. Both
        // questions are cheap -- one local daemon lookup, and one HTTPS request that is only asked
        // when there is something to fall back to -- and together they turn those minutes into
        // seconds.
        bool canBuildLocally = plan.RuntimeImage is { } localBase
            && plan.AssemblyFileName is { Length: > 0 }
            && await ImageExistsLocallyAsync(localBase, cancellationToken).ConfigureAwait(false);

        if (plan.RuntimeImage is { } wantedImage
            && plan.AssemblyFileName is { Length: > 0 }
            && !canBuildLocally
            && !await ContainerRegistryProbe.IsReachableAsync(wantedImage, cancellationToken).ConfigureAwait(false))
        {
            // The registry is out of reach and the base image is not here either, which used to be
            // the end of it. It no longer is: the daemon cannot fetch the image, but this process
            // can, over a path the daemon does not use.
            canBuildLocally = await ContainerImagePull.FetchOverIPv4Async(wantedImage, logger, cancellationToken).ConfigureAwait(false);
        }

        if (canBuildLocally
            && plan.RuntimeImage is { } declaredBase
            && !await ContainerRegistryProbe.IsReachableAsync(declaredBase, cancellationToken).ConfigureAwait(false))
        {
            logger.LogWarning(
                $"'{ContainerRegistryProbe.RegistryHostOf(declaredBase)}' did not answer within {ContainerRegistryProbe.Timeout:g}, and the SDK needs it to build an image even though "
                + $"'{declaredBase}' is already in the local daemon. Building from the local copy instead, which contacts no registry.");

            ContainerSourcePlan? early = await BuildFromLocalBaseAsync(
                plan, identifier, repository, tag, declaredBase, logger, cancellationToken).ConfigureAwait(false);

            if (early is not null)
                return early;

            logger.LogWarning("Building from the local copy did not work either, so the SDK is asked after all. This may take a few minutes before it fails.");
        }

        List<string> arguments =
        [
            "publish",
            plan.ProjectPath!,
            "-c", plan.Configuration!,
            "-f", plan.TargetFramework!,
            "-t:PublishContainer",
            "-p:EnableSdkContainerSupport=true",
            $"-p:ContainerRepository={repository}",
            $"-p:ContainerImageTag={tag}",

            // ContainerLabel is an MSBuild item, and an item cannot be supplied from the command line.
            // ContainerVendor is a property and the SDK turns it into the OCI vendor annotation, so it
            // is the one label an SDK publish can be given here — and the sweep needs a label to find
            // this image by after a killed run.
            $"-p:ContainerVendor={ContainerLeftovers.VendorLabelValue}",
        ];

        if (plan.RuntimeImage is { } runtimeImage)
            arguments.Add($"-p:ContainerBaseImage={runtimeImage}");

        // How long to wait is decided by what being wrong costs. With the base image already here,
        // a publish that stops making progress is not a lost run -- it is a two minute detour -- so
        // there is no reason to sit through ten minutes of it. With nothing to fall back on, giving
        // up early would turn a slow machine into a failed run, so it gets the full budget.
        //
        // This matters because the probe above is a hint: it is wrong roughly one time in five on the
        // network this was written for, and without the shorter budget being wrong costs the whole
        // ten minutes.
        TimeSpan publishTimeout = canBuildLocally ? SdkPublishTimeoutWithLocalFallback : DotNetCli.DefaultTimeout;

        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        DotNetCliResult result = await DotNetCli
            .RunAsync(arguments, Path.GetDirectoryName(plan.ProjectPath), publishTimeout, cancellationToken)
            .ConfigureAwait(false);
        stopwatch.Stop();

        if (result.TimedOut && canBuildLocally)
        {
            logger.LogWarning(
                $"The SDK publish for '{identifier}' made no progress within {publishTimeout:g} and was stopped. The base image is already here, so the image is built from it instead.");
        }

        if (!result.Succeeded)
        {
            // A publish that wedged counts as much as one that failed. When the registry neither
            // answers nor refuses, the SDK does not report anything at all -- so waiting for it to
            // say "unreachable" would mean never recovering in the very case that costs the most.
            if ((IsRegistryUnreachable(result) || result.TimedOut) && plan.RuntimeImage is { } wantedBase)
            {
                ContainerSourcePlan? recovered = await TryRecoverFromLocalBaseAsync(
                    plan, identifier, repository, tag, wantedBase, logger, cancellationToken).ConfigureAwait(false);

                if (recovered is not null)
                    return recovered;
            }

            throw new FrameworkConfigurationException(
                $"The SDK could not build an image for '{identifier}' from '{plan.ProjectPath}'.",
                [.. RecoveryStepsFor(result, plan)],
                [result.Describe()]);
        }

        string image = $"{repository}:{tag}";
        logger.LogInformation("'{0}' built image '{1}' in {2}.", identifier, image, stopwatch.Elapsed);

        return plan with { Image = image, BuiltAtUtc = DateTimeOffset.UtcNow };
    }

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

    /// <summary>
    /// Whether the daemon already has this image, so a build can use it without a registry.
    /// </summary>
    internal static async Task<bool> ImageExistsLocallyAsync(string image, CancellationToken cancellationToken)
    {
        ContainerDockerCommands.CommandResult result = await ContainerDockerCommands
            .RunAsync($"image inspect {image}", TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);

        return result.ExitCode == 0;
    }

    /// <summary>
    /// Whether a failed build is the registry being unreachable rather than the project being broken.
    /// </summary>
    internal static bool IsRegistryUnreachable(DotNetCliResult result)
    {
        string output = $"{result.StandardOutput}\n{result.StandardError}";

        return output.Contains("CONTAINER1006", StringComparison.OrdinalIgnoreCase)
            || output.Contains("SSL connection could not be established", StringComparison.OrdinalIgnoreCase)
            || output.Contains("SecureConnectionError", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Unable to read data from the transport connection", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ContainerSourcePlan> BuildInContainerAsync(
        ContainerSourcePlan plan,
        string identifier,
        ScopedLogger logger,
        CancellationToken cancellationToken)
    {
        await EnsureLinuxContainersAsync(cancellationToken).ConfigureAwait(false);

        // Read again rather than carried on the plan, so the plan stays a description and this stays
        // the only place that needs the project's own details.
        ProjectFacts facts = await ProjectQuery.ReadAsync(plan.ProjectPath!, cancellationToken).ConfigureAwait(false);
        return await InContainerBuild.BuildAsync(plan, facts, identifier, logger, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ContainerSourcePlan> PublishToDirectoryAsync(
        ContainerSourcePlan plan,
        string identifier,
        ScopedLogger logger,
        CancellationToken cancellationToken)
    {
        string output = Path.Combine(Path.GetTempPath(), $"tf-{ToRepositoryName(identifier)}-{Guid.NewGuid().ToString("N")[..8]}");

        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        DotNetCliResult result = await DotNetCli.RunAsync(
            [
                "publish",
                plan.ProjectPath!,
                "-c", plan.Configuration!,
                "-f", plan.TargetFramework!,
                "-o", output,
            ],
            Path.GetDirectoryName(plan.ProjectPath),
            cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        if (!result.Succeeded)
        {
            throw new FrameworkConfigurationException(
                $"The project for '{identifier}' could not be published.",
                ["The full command output follows the message in the run log."],
                [result.Describe()]);
        }

        logger.LogInformation("'{0}' published to '{1}' in {2}.", identifier, output, stopwatch.Elapsed);
        return plan with { OutputDirectory = output, BuiltAtUtc = DateTimeOffset.UtcNow };
    }

    /// <summary>
    /// Turns a failed SDK image build into steps that address what actually went wrong.
    /// </summary>
    /// <remarks>
    /// Building an image makes the SDK fetch the base image's manifest from a registry over TLS,
    /// and that request fails on more networks than one would expect. The raw output in that case is
    /// a wall of retries ending in CONTAINER1006, which says nothing about the cause, so the two
    /// recognisable network failures get named here instead of being left to the reader.
    ///
    /// The one seen most often is a broken IPv6 path. A registry hostname usually resolves to both
    /// families; the connection is made over IPv6, TCP succeeds because the first packets are small,
    /// and then the handshake dies on the certificate chain because those packets exceed the real
    /// path MTU. Routers cannot fragment IPv6, and the ICMPv6 message that would report it is often
    /// filtered, so the packets simply disappear. A PPPoE line (MTU 1492) behind a VPN is the
    /// classic setting. It is also intermittent, because the client races both families and IPv4
    /// sometimes wins - which is why a retry occasionally succeeds and hides the cause.
    /// </remarks>
    internal static IReadOnlyList<string> RecoveryStepsFor(DotNetCliResult result, ContainerSourcePlan plan)
    {
        if (result.TimedOut)
        {
            return
            [
                $"The publish was killed after {DotNetCli.DefaultTimeout:g} without answering. It had not failed - it had stopped making progress.",
                $"Reaching the registry for the base image '{plan.RuntimeImage ?? "(the project's default)"}' is the usual reason: a registry that neither answers nor refuses leaves the SDK waiting rather than reporting anything.",
                $"Fetch the base image once on a working network - `docker pull {plan.RuntimeImage ?? "<base image>"}` - and later runs build from the local copy without contacting a registry at all.",
                "Where the machine cannot be changed, hand the source a prebuilt image instead of a project: ContainerSource.Image(\"my-api:local\"). Nothing is fetched at run time.",
            ];
        }

        if (!IsRegistryUnreachable(result))
        {
            return
            [
                "Check that the project publishes on its own.",
                "The full command output follows the message in the run log.",
            ];
        }

        return
        [
            $"The SDK could not reach the registry for the base image '{plan.RuntimeImage ?? "(the project's default)"}'. The project itself is fine; the build never got that far.",
            $"Building from a locally cached base image was tried and did not apply here, which means '{plan.RuntimeImage ?? "the base image"}' is not in the local daemon either. Fetch it once on a working network - `docker pull {plan.RuntimeImage ?? "<base image>"}` - and this run will build without a registry from then on.",
            "If that pull fails too, the network is the problem rather than anything in this project. Plain HTTPS to the registry working while a pull does not points at IPv6: compare `curl -4` against `curl -6` on the registry host.",
            "That failure is a path-MTU problem rather than a firewall, and it needs no code change - lowering the MTU (a PPPoE line is 1492, not 1500) or preferring IPv4 resolves it. Both need administrator rights.",
            "Where the machine cannot be changed at all, hand the source a prebuilt image instead of a project: ContainerSource.Image(\"my-api:local\"). Nothing is fetched at run time.",
            "The full command output follows the message in the run log.",
        ];
    }

    /// <summary>
    /// Converts an identifier into a name a container registry accepts.
    /// </summary>
    /// <param name="identifier">The identifier.</param>
    public static string ToRepositoryName(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);

        char[] characters = identifier.ToLowerInvariant().ToCharArray();
        for (int index = 0; index < characters.Length; index++)
        {
            if (!char.IsAsciiLetterLower(characters[index]) && !char.IsAsciiDigit(characters[index]))
                characters[index] = '-';
        }

        string cleaned = new string(characters).Trim('-');
        return string.IsNullOrEmpty(cleaned) ? "testframework-app" : $"testframework-{cleaned}";
    }

    private static async Task EnsureDockerCliAsync(CancellationToken cancellationToken)
    {
        // The SDK shells out to a docker or podman executable rather than talking to the daemon, so
        // a machine that can run containers can still be unable to build an image this way.
        try
        {
            ContainerDockerCommands.CommandResult result = await ContainerDockerCommands.RunAsync("--version", cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 0)
                return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw DockerCliMissing(exception);
        }

        throw DockerCliMissing(null);
    }

    private static async Task EnsureLinuxContainersAsync(CancellationToken cancellationToken)
    {
        ContainerDockerCommands.CommandResult result = await ContainerDockerCommands
            .RunAsync("info --format {{.OSType}}", cancellationToken)
            .ConfigureAwait(false);

        string osType = result.StandardOutput.Trim();
        if (result.ExitCode != 0 || osType.Length == 0 || string.Equals(osType, "linux", StringComparison.OrdinalIgnoreCase))
            return;

        // The base images this framework selects are Linux images. Saying so now beats an image that
        // builds and then cannot start.
        throw new FrameworkConfigurationException(
            $"The Docker daemon is serving '{osType}' containers, and the .NET base images used here are Linux images.",
            [
                "Switch Docker Desktop to Linux containers.",
                "Or name a matching image with WithRuntimeImage(\"...\").",
            ]);
    }

    private static FrameworkConfigurationException DockerCliMissing(Exception? innerException)
        => new(
            "No 'docker' executable was found, and the .NET SDK needs one to build a container image.",
            [
                "Install the Docker CLI, or use BuiltOnHost() which needs no executable.",
                "On a machine that reaches a remote daemon through DOCKER_HOST, the CLI still has to be installed locally.",
            ],
            null,
            innerException);
}
