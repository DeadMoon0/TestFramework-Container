using System;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Core.Exceptions;
using TestFramework.Core.Logging;

namespace TestFramework.Container;

/// <summary>
/// Asks Docker, once, whether it can run this package's containers - and says which of three things is wrong when
/// it cannot.
/// </summary>
/// <remarks>
/// <para>
/// Without it, "Docker is not here" surfaced as whatever touched Docker first: a <c>Win32Exception</c> from starting
/// the CLI, Testcontainers' own error from creating the network, or nothing at all - the image builder's check
/// returned early on a failed <c>docker info</c>, so a stopped daemon passed it.
/// </para>
/// <para>
/// <c>docker version</c> rather than <c>docker info</c>: it answers fast, it fails when the daemon does not answer,
/// and its server half says which kind of containers the daemon serves.
/// </para>
/// </remarks>
internal static class DockerProbe
{
    /// <summary>The command that asks the daemon which operating system its containers run.</summary>
    internal const string Arguments = "version --format {{.Server.Os}}";

    /// <summary>How long the daemon has to answer. A healthy one answers in well under a second.</summary>
    internal static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Asks the daemon, and refuses when it cannot serve this package's containers.
    /// </summary>
    /// <exception cref="FrameworkConfigurationException">No CLI, no daemon answering, or a daemon serving Windows containers.</exception>
    internal static async Task RequireAsync(ScopedLogger? logger, CancellationToken cancellationToken)
    {
        ContainerDockerCommands.CommandResult result = await ContainerDockerCommands.RunAsync(Arguments, Ceiling, cancellationToken).ConfigureAwait(false);

        if (Refusal(result, Host()) is { } refusal)
            throw refusal;

        logger?.LogInformation($"Docker answered at {Host()} and serves Linux containers.");
    }

    /// <summary>
    /// What the probe's answer means: nothing wrong, or the refusal to raise.
    /// </summary>
    /// <param name="result">What <c>docker version</c> returned.</param>
    /// <param name="host">Where the client looked for the daemon, for the message.</param>
    internal static FrameworkConfigurationException? Refusal(ContainerDockerCommands.CommandResult result, string host)
    {
        string os = result.StandardOutput.Trim();

        if (result.ExitCode != 0 || os.Length == 0)
            return DaemonNotAnswering(host, result.StandardError.Trim());

        return string.Equals(os, "linux", StringComparison.OrdinalIgnoreCase) ? null : NotLinux(os);
    }

    /// <summary>
    /// The refusal for a machine with no <c>docker</c> executable on its PATH.
    /// </summary>
    internal static FrameworkConfigurationException CliMissing(Exception innerException)
        => new("No 'docker' executable was found. This package runs its containers and removes them through the Docker CLI.", innerException)
        {
            RecoverySteps =
            [
                "Install Docker Desktop, or the Docker CLI, and make sure 'docker' is on the PATH of the process running the tests.",
                "A machine that reaches a remote daemon through DOCKER_HOST still needs the CLI installed locally.",
            ],
        };

    private static FrameworkConfigurationException DaemonNotAnswering(string host, string detail)
        => new($"The Docker daemon at {host} did not answer{(detail.Length > 0 ? $": {detail}" : ".")}")
        {
            RecoverySteps =
            [
                "Start Docker Desktop, or the Docker service, and wait until it reports that it is running.",
                "If DOCKER_HOST is set, check that it names the daemon you mean.",
            ],
        };

    private static FrameworkConfigurationException NotLinux(string os)
        => new($"The Docker daemon serves '{os}' containers. Every image this package runs - SQL Server, the emulators, the .NET base images - is a Linux image.")
        {
            RecoverySteps = ["Switch Docker Desktop to Linux containers."],
        };

    private static string Host()
        => Environment.GetEnvironmentVariable("DOCKER_HOST") is { Length: > 0 } host ? $"'{host}'" : "the default Docker endpoint";
}
