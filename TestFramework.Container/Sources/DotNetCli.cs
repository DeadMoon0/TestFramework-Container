using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TestFramework.Container.Sources;

/// <summary>
/// The outcome of a <c>dotnet</c> invocation.
/// </summary>
/// <param name="Arguments">The arguments the process was started with.</param>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="StandardOutput">Captured standard output.</param>
/// <param name="StandardError">Captured standard error.</param>
public sealed record DotNetCliResult(IReadOnlyList<string> Arguments, int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>
    /// Whether the process reported success.
    /// </summary>
    public bool Succeeded => ExitCode == 0;

    /// <summary>
    /// Whether the process was killed for outrunning its timeout rather than reporting a result.
    /// </summary>
    /// <remarks>
    /// Worth telling apart from an ordinary failure: a command that never answered has produced no
    /// diagnosis to read, so the caller has to reason about what it was trying to do instead of
    /// about what it said.
    /// </remarks>
    public bool TimedOut { get; init; }

    /// <summary>
    /// Returns the command and its output, for an error message that can be acted on.
    /// </summary>
    public string Describe()
    {
        List<string> lines = [$"dotnet {string.Join(" ", Arguments)} exited with {ExitCode}."];

        if (!string.IsNullOrWhiteSpace(StandardOutput))
            lines.Add(StandardOutput.Trim());

        if (!string.IsNullOrWhiteSpace(StandardError))
            lines.Add(StandardError.Trim());

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Runs the <c>dotnet</c> command line.
/// </summary>
/// <remarks>
/// Arguments are passed as a list rather than as one string, so a path containing a space -- which
/// is the normal case under a Windows user profile -- cannot be split into two arguments. The same
/// code runs on a Windows and a Linux host; <c>dotnet</c> is resolved from the path either way.
/// </remarks>
public static class DotNetCli
{
    /// <summary>
    /// How long a <c>dotnet</c> invocation may run before it is killed.
    /// </summary>
    /// <remarks>
    /// Generous, because a cold restore and compile is minutes of legitimate work -- but finite,
    /// which the earlier unbounded wait was not. An SDK container publish can wedge indefinitely
    /// when the registry it reaches for neither answers nor refuses: observed here as publishes
    /// still alive after ninety minutes, each holding a hung <c>docker</c> child, and surviving the
    /// test run that started them. Without a bound, nothing downstream ever gets the chance to
    /// recover, because it is still waiting for a result that will not come.
    /// </remarks>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Runs <c>dotnet</c> and captures its output.
    /// </summary>
    /// <param name="arguments">The arguments, one element per argument.</param>
    /// <param name="workingDirectory">The working directory, or <see langword="null"/> for the current one.</param>
    /// <param name="cancellationToken">The cancellation token for the running command.</param>
    public static Task<DotNetCliResult> RunAsync(IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken cancellationToken)
        => RunAsync(arguments, workingDirectory, DefaultTimeout, cancellationToken);

    /// <summary>
    /// Runs <c>dotnet</c> with an explicit timeout and captures its output.
    /// </summary>
    /// <param name="arguments">The arguments, one element per argument.</param>
    /// <param name="workingDirectory">The working directory, or <see langword="null"/> for the current one.</param>
    /// <param name="timeout">How long the command may run. Use <see cref="Timeout.InfiniteTimeSpan"/> for no limit.</param>
    /// <param name="cancellationToken">The cancellation token for the running command.</param>
    /// <remarks>
    /// A command that outruns its timeout is killed, process tree and all, and comes back as a failed
    /// result rather than an exception, so a caller that can recover still gets the chance to.
    ///
    /// The process tree is killed on cancellation too. Awaiting a process is not the same as owning
    /// it: a cancelled wait leaves the process running, which is how a stopped test run left a
    /// publish and its <c>docker</c> child behind to idle for an hour.
    /// </remarks>
    public static async Task<DotNetCliResult> RunAsync(
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();

        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        // A build that inherits a parent MSBuild's environment picks up its targets and node reuse,
        // which makes a nested invocation behave differently than the same command in a shell.
        startInfo.Environment.Remove("MSBuildLoadMicrosoftTargetsReadOnly");
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        // A Windows test host commonly carries DOCKER_HOST in the short named-pipe form. The SDK's
        // container tooling cannot use that form and reports it as a missing docker executable, so
        // the value is rewritten before it is inherited.
        ContainerDockerCommands.NormalizeDockerHost(startInfo.Environment);

        using Process process = new() { StartInfo = startInfo };
        process.Start();

        using CancellationTokenSource expiry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
            expiry.CancelAfter(timeout);

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(expiry.Token);
        Task<string> standardError = process.StandardError.ReadToEndAsync(expiry.Token);

        try
        {
            await process.WaitForExitAsync(expiry.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillProcessTree(process);
            return new DotNetCliResult(
                [.. arguments],
                -1,
                string.Empty,
                $"'dotnet {string.Join(" ", arguments)}' did not finish within {timeout:g} and was terminated.")
            {
                TimedOut = true,
            };
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            throw;
        }

        return new DotNetCliResult(
            [.. arguments],
            process.ExitCode,
            await ReadOrEmptyAsync(standardOutput).ConfigureAwait(false),
            await ReadOrEmptyAsync(standardError).ConfigureAwait(false));
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
            // It finished on its own between the check and the kill, or the platform will not allow
            // it. Neither is worth failing over.
        }
    }

    private static async Task<string> ReadOrEmptyAsync(Task<string> read)
    {
        try
        {
            return await read.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or System.IO.IOException or ObjectDisposedException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Returns the major and minor version of the SDK on the path, for example <c>10.0</c>.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for the running command.</param>
    /// <remarks>
    /// This is the SDK that is already known to build the project, which makes it the right default
    /// when an image tag has to be chosen.
    /// </remarks>
    public static async Task<string?> ReadSdkMajorMinorAsync(CancellationToken cancellationToken)
    {
        DotNetCliResult result = await RunAsync(["--version"], null, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
            return null;

        string[] parts = result.StandardOutput.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : null;
    }
}
