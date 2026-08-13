using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace TestFramework.Container;

/// <summary>
/// Removes Docker resources reliably, falling back to the client library when the CLI is unavailable.
/// </summary>
/// <remarks>
/// Disposing through the client can leave a container or network behind when the daemon is busy, so
/// teardown asks the CLI first and only then falls back. Teardown must not fail a run.
/// </remarks>
public static class ContainerDockerCommands
{
    /// <summary>
    /// Removes a container, falling back to disposing it.
    /// </summary>
    /// <param name="container">The container to remove.</param>
    /// <param name="cancellationToken">The cancellation token for the running teardown.</param>
    public static async Task ForceRemoveContainerAsync(IContainer container, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(container);

        try
        {
            CommandResult result = await RunAsync($"rm -f {container.Id}", cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 0)
                return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }

        await container.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a network, falling back to disposing it.
    /// </summary>
    /// <param name="network">The network to remove.</param>
    /// <param name="cancellationToken">The cancellation token for the running teardown.</param>
    public static async Task ForceRemoveNetworkAsync(INetwork network, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(network);

        try
        {
            CommandResult result = await RunAsync($"network rm {network.Name}", cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 0)
                return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }

        await network.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a Docker CLI command and captures its output.
    /// </summary>
    /// <param name="arguments">The arguments passed to the docker executable.</param>
    /// <param name="cancellationToken">The cancellation token for the running command.</param>
    public static async Task<CommandResult> RunAsync(string arguments, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(arguments);
        cancellationToken.ThrowIfCancellationRequested();

        ProcessStartInfo startInfo = new()
        {
            FileName = "docker",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        NormalizeDockerHost(startInfo.Environment);

        using Process process = new()
        {
            StartInfo = startInfo,
        };

        process.Start();
        string standardOutput = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        string standardError = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return new CommandResult(process.ExitCode, standardOutput, standardError);
    }

    /// <summary>
    /// Rewrites a Windows named-pipe DOCKER_HOST value into the form the CLI accepts.
    /// </summary>
    /// <param name="environment">The environment block passed to the process.</param>
    public static void NormalizeDockerHost(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        const string dockerHost = "DOCKER_HOST";

        if (!environment.TryGetValue(dockerHost, out string? value) || string.IsNullOrWhiteSpace(value))
            return;

        if (value.StartsWith("npipe://./pipe/", StringComparison.OrdinalIgnoreCase))
            environment[dockerHost] = $"npipe:////./pipe/{value[15..]}";
    }

    /// <summary>
    /// The outcome of a Docker CLI command.
    /// </summary>
    /// <param name="ExitCode">The process exit code.</param>
    /// <param name="StandardOutput">Captured standard output.</param>
    /// <param name="StandardError">Captured standard error.</param>
    public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);
}
