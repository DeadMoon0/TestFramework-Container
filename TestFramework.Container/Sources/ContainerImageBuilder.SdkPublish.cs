using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Core.Exceptions;
using TestFramework.Core.Logging;

namespace TestFramework.Container.Sources;

public static partial class ContainerImageBuilder
{
    /// <summary>
    /// How long the SDK publish may run when the base image is already in the local daemon.
    /// </summary>
    /// <remarks>
    /// Deliberately impatient, and only used when there is somewhere to go: a build that stops making
    /// progress here costs a short detour rather than the run, so waiting the full budget out buys
    /// nothing. Long enough that a genuine cold restore and compile still finishes.
    /// </remarks>
    internal static readonly TimeSpan SdkPublishTimeoutWithLocalFallback = TimeSpan.FromMinutes(3);

    private static async Task<ContainerSourcePlan> PublishImageAsync(
        ContainerSourcePlan plan,
        string identifier,
        ScopedLogger logger,
        CancellationToken cancellationToken)
    {
        await ContainerRuntime.EnsureReadyAsync(logger, cancellationToken).ConfigureAwait(false);

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
                $"The SDK could not build an image for '{identifier}' from '{plan.ProjectPath}'.")
            {
                RecoverySteps = [.. RecoveryStepsFor(result, plan)],
                AvailableOptions = [result.Describe()],
            };
        }

        string image = $"{repository}:{tag}";
        logger.LogInformation("'{0}' built image '{1}' in {2}.", identifier, image, stopwatch.Elapsed);

        return plan with { Image = image, BuiltAtUtc = DateTimeOffset.UtcNow };
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
}
