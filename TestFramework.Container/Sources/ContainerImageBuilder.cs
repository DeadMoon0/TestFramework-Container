using System;
using System.Collections.Concurrent;
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
public static partial class ContainerImageBuilder
{
    /// <summary>
    /// The images built in this process, by build identity, each with the source fingerprint it was built from.
    /// </summary>
    private static readonly ConcurrentDictionary<string, BuiltImage> BuiltImages = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> BuildGates = new(StringComparer.Ordinal);

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
            return await PublishToDirectoryOnceAtATimeAsync(plan, identifier, logger, cancellationToken).ConfigureAwait(false);

        return await BuildImageOnceAsync(plan, identifier, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes a project to its own directory, one publish at a time per project.
    /// </summary>
    /// <remarks>
    /// Every caller gets its own output directory, so the outputs cannot collide -- and for a long
    /// time that looked like enough. It is not: they all drive MSBuild over one project, and MSBuild
    /// keeps its intermediate state in that project's <c>obj/</c> whatever <c>-o</c> says. Two
    /// environments publishing the same project at once take that state from each other, and the one
    /// that loses reports that the project could not be published.
    ///
    /// This is reached far more often than it used to be. A Function App payload is mounted into the
    /// Functions host image rather than run as an image of its own, so every Function App is on this
    /// strategy -- and a suite that puts two of them in test classes xunit runs in parallel hits it
    /// immediately.
    ///
    /// Unlike an image, the result is deliberately not shared between callers. A published directory
    /// is mounted rather than layered, and each environment deletes its own at teardown, so handing a
    /// second environment the first one's directory would hand it one that is about to be removed.
    /// The gate buys correctness here, not the rebuild that image reuse saves.
    ///
    /// The gate has two layers on purpose. The semaphore serializes the environments of this process;
    /// the machine-wide lock behind it serializes the processes, because a multi-targeted test suite
    /// runs one process per framework at the same time and each has its own semaphores. The second
    /// layer is where a Function App suite died on CI: two test hosts published the same project at
    /// once, and the worker SDK's nested build lost its deps file to the other process.
    /// </remarks>
    private static async Task<ContainerSourcePlan> PublishToDirectoryOnceAtATimeAsync(
        ContainerSourcePlan plan,
        string identifier,
        ScopedLogger logger,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim gate = GateFor(plan);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using (await EnterMachineWideAsync(plan, cancellationToken).ConfigureAwait(false))
            {
                return await PublishToDirectoryAsync(plan, identifier, logger, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Builds the image for a plan, or hands back the one an earlier environment in this process built from the
    /// same, unchanged sources.
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
    /// The gate is per project rather than global, so two different applications still build at the
    /// same time while two environments wanting the same application do not race each other into
    /// running two publishes over one project directory. Reuse stays keyed on the full identity: what
    /// may not happen at once is broader than what counts as the same image.
    /// </remarks>
    private static async Task<ContainerSourcePlan> BuildImageOnceAsync(
        ContainerSourcePlan plan,
        string identifier,
        ScopedLogger logger,
        CancellationToken cancellationToken)
    {
        string identity = BuildIdentityOf(plan);
        SemaphoreSlim gate = GateFor(plan);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Read under the gate, so a build that just finished is compared with the sources it was built from.
            string fingerprint = plan.ProjectPath is { Length: > 0 } projectPath ? SourceFingerprint.Of(projectPath) : string.Empty;

            if (await TryReuseAsync(identity, fingerprint, plan, identifier, logger, cancellationToken).ConfigureAwait(false) is { } reused)
                return reused;

            // The same two-layer gate as the host publish: an SDK image build drives MSBuild over the
            // same obj/ directory, and another test process may be doing the same right now.
            using (await EnterMachineWideAsync(plan, cancellationToken).ConfigureAwait(false))
            {
                ContainerSourcePlan built = plan.Strategy switch
                {
                    ContainerBuildStrategy.SdkContainerPublish => await PublishImageAsync(plan, identifier, logger, cancellationToken).ConfigureAwait(false),
                    ContainerBuildStrategy.InContainer => await BuildInContainerAsync(plan, identifier, logger, cancellationToken).ConfigureAwait(false),
                    _ => throw new FrameworkConfigurationException($"The build strategy '{plan.Strategy}' is not supported."),
                };

                if (built.Image is { Length: > 0 })
                    BuiltImages[identity] = new BuiltImage(built, fingerprint);

                return built;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<ContainerSourcePlan?> TryReuseAsync(
        string identity,
        string fingerprint,
        ContainerSourcePlan plan,
        string identifier,
        ScopedLogger logger,
        CancellationToken cancellationToken)
    {
        if (!BuiltImages.TryGetValue(identity, out BuiltImage? cached) || cached.Plan.Image is not { Length: > 0 } image)
            return null;

        ContainerSourcePlan earlier = cached.Plan;
        if (!string.Equals(cached.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            // Dropped rather than kept beside the new one: the old image is now only a leftover, and an entry
            // here is what stops teardown from removing it.
            BuiltImages.TryRemove(identity, out _);
            logger.LogInformation("'{0}' does not reuse image '{1}': its sources changed since it was built, so it is built again.", identifier, image);
            return null;
        }

        // Asked rather than assumed: a developer clearing images between chapters, or a sweep on a
        // shared machine, would otherwise leave this handing out the name of an image that is gone,
        // and the failure would surface as a container that will not start.
        if (!await ImageExistsLocallyAsync(image, cancellationToken).ConfigureAwait(false))
        {
            BuiltImages.TryRemove(identity, out _);
            return null;
        }

        logger.LogInformation(
            "'{0}' is reusing image '{1}', built earlier in this process from the same sources, configuration, framework and base image.",
            identifier,
            image);

        return plan with { Image = image, BuiltAtUtc = earlier.BuiltAtUtc, FallbackReason = earlier.FallbackReason };
    }

    /// <summary>
    /// Forgets which images this process built, so the next build produces a fresh one.
    /// </summary>
    internal static void ForgetBuiltImages() => BuiltImages.Clear();

    /// <summary>
    /// The gate a plan queues on: everything built from one project waits for everything else built
    /// from that project.
    /// </summary>
    /// <remarks>
    /// Deliberately coarser than the build identity. Two plans differing only in configuration, or in
    /// whether the SDK builds the image or the host publishes the output, are two different results --
    /// but they are one project directory, and MSBuild will not have its <c>obj/</c> driven from two
    /// places at once. Keying the gate on the identity instead let exactly those pairs race.
    ///
    /// Different projects still build at the same time, which is the parallelism worth keeping.
    /// </remarks>
    private static SemaphoreSlim GateFor(ContainerSourcePlan plan)
        => BuildGates.GetOrAdd(plan.ProjectPath ?? string.Empty, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// The cross-process layer of the project gate. Taken inside the semaphore, so lock order is the
    /// same everywhere: this process first, then the machine.
    /// </summary>
    /// <param name="plan">The plan whose project is about to be built.</param>
    /// <param name="cancellationToken">Cancels the waiting.</param>
    /// <returns>The held lock, or nothing to hold for a plan without a project.</returns>
    private static async Task<IDisposable?> EnterMachineWideAsync(ContainerSourcePlan plan, CancellationToken cancellationToken)
        => plan.ProjectPath is { Length: > 0 } projectPath
            ? await MachineWideProjectGate.EnterAsync(projectPath, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>
    /// The inputs that fully decide what an image built from this plan contains.
    /// </summary>
    /// <remarks>
    /// The sources are not part of the identity but are checked beside it (<see cref="SourceFingerprint"/>), so an
    /// edit replaces the cached image instead of adding a second one for the same inputs.
    /// </remarks>
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

        foreach (BuiltImage built in BuiltImages.Values)
        {
            if (string.Equals(built.Plan.Image, image, StringComparison.Ordinal))
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

    private static async Task<ContainerSourcePlan> BuildInContainerAsync(
        ContainerSourcePlan plan,
        string identifier,
        ScopedLogger logger,
        CancellationToken cancellationToken)
    {
        await ContainerRuntime.EnsureReadyAsync(logger, cancellationToken).ConfigureAwait(false);

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
                $"The project for '{identifier}' could not be published.")
            {
                RecoverySteps = ["The full command output follows the message in the run log."],
                AvailableOptions = [result.Describe()],
            };
        }

        logger.LogInformation("'{0}' published to '{1}' in {2}.", identifier, output, stopwatch.Elapsed);
        return plan with { OutputDirectory = output, BuiltAtUtc = DateTimeOffset.UtcNow };
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
}
