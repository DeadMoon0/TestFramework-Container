using System;
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

        if (plan.Kind != ContainerSourceKind.Project)
            return plan;

        return plan.Strategy switch
        {
            ContainerBuildStrategy.SdkContainerPublish => await PublishImageAsync(plan, identifier, logger, cancellationToken).ConfigureAwait(false),
            ContainerBuildStrategy.HostPublish => await PublishToDirectoryAsync(plan, identifier, logger, cancellationToken).ConfigureAwait(false),
            ContainerBuildStrategy.InContainer => await BuildInContainerAsync(plan, identifier, logger, cancellationToken).ConfigureAwait(false),
            _ => throw new FrameworkConfigurationException($"The build strategy '{plan.Strategy}' is not supported."),
        };
    }

    /// <summary>
    /// Removes an image this builder produced.
    /// </summary>
    /// <param name="image">The image reference.</param>
    /// <param name="cancellationToken">The cancellation token for the running teardown.</param>
    public static async Task RemoveImageAsync(string image, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);

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
        ];

        if (plan.RuntimeImage is { } runtimeImage)
            arguments.Add($"-p:ContainerBaseImage={runtimeImage}");

        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        DotNetCliResult result = await DotNetCli.RunAsync(arguments, Path.GetDirectoryName(plan.ProjectPath), cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        if (!result.Succeeded)
        {
            throw new FrameworkConfigurationException(
                $"The SDK could not build an image for '{identifier}' from '{plan.ProjectPath}'.",
                [
                    "Check that the project publishes on its own.",
                    "The full command output follows the message in the run log.",
                ],
                [result.Describe()]);
        }

        string image = $"{repository}:{tag}";
        logger.LogInformation("'{0}' built image '{1}' in {2}.", identifier, image, stopwatch.Elapsed);

        return plan with { Image = image, BuiltAtUtc = DateTimeOffset.UtcNow };
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
