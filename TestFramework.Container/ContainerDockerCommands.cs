using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    /// How long a Docker CLI command may run before it is killed.
    /// </summary>
    /// <remarks>
    /// A wedged CLI is worse than a failing one: teardown calls this and never returns, so the test
    /// host never exits and CI waits for its own job timeout instead of reporting a failure.
    /// </remarks>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a build may run before it is killed.
    /// </summary>
    /// <remarks>
    /// A cold restore and compile inside a container is minutes of legitimate work, so a build gets its
    /// own budget rather than the one sized for <c>rm -f</c>.
    /// </remarks>
    public static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Runs a Docker CLI command and captures its output.
    /// </summary>
    /// <param name="arguments">The arguments passed to the docker executable.</param>
    /// <param name="cancellationToken">The cancellation token for the running command.</param>
    /// <remarks>
    /// A command that outruns its timeout is killed, process tree and all, and comes back as a non-zero
    /// result rather than an exception, so the callers that fall back on a failed CLI keep working.
    /// </remarks>
    public static Task<CommandResult> RunAsync(string arguments, CancellationToken cancellationToken)
        => RunAsync(arguments, ChooseTimeout(arguments), cancellationToken);

    /// <summary>
    /// Runs a Docker CLI command with an explicit timeout.
    /// </summary>
    /// <param name="arguments">The arguments passed to the docker executable.</param>
    /// <param name="timeout">How long the command may run. Use <see cref="Timeout.InfiniteTimeSpan"/> for no limit.</param>
    /// <param name="cancellationToken">The cancellation token for the running command.</param>
    public static async Task<CommandResult> RunAsync(string arguments, TimeSpan timeout, CancellationToken cancellationToken)
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

        using CancellationTokenSource expiry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
            expiry.CancelAfter(timeout);

        // Both streams are drained at once. Reading one to the end first deadlocks as soon as the
        // other fills its pipe buffer, which a verbose command such as 'docker build' does within a
        // second: it writes its whole progress report to standard error.
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(expiry.Token);
        Task<string> standardError = process.StandardError.ReadToEndAsync(expiry.Token);

        try
        {
            await process.WaitForExitAsync(expiry.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller did not cancel, so this is the timeout. Everything a wedged CLI has written so
            // far is lost with it, but the run gets its thread back.
            KillProcessTree(process);
            return new CommandResult(-1, string.Empty, $"'docker {arguments}' did not finish within {timeout:g} and was terminated.");
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            throw;
        }

        return new CommandResult(
            process.ExitCode,
            await ReadOrEmptyAsync(standardOutput).ConfigureAwait(false),
            await ReadOrEmptyAsync(standardError).ConfigureAwait(false));
    }

    private static TimeSpan ChooseTimeout(string arguments)
    {
        // 'docker build' is the one command whose legitimate runtime is measured in minutes.
        return arguments.StartsWith("build ", StringComparison.OrdinalIgnoreCase) || string.Equals(arguments, "build", StringComparison.OrdinalIgnoreCase)
            ? BuildTimeout
            : DefaultTimeout;
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // The process finished on its own between the check and the kill, or the platform will not
            // let it be killed. Neither is worth failing over.
        }
    }

    private static async Task<string> ReadOrEmptyAsync(Task<string> read)
    {
        try
        {
            return await read.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
            return string.Empty;
        }
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
