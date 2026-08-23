using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TestFramework.Container.Web.Sites;

/// <summary>
/// The outcome of an <c>npm</c> invocation.
/// </summary>
/// <param name="Arguments">The arguments the process was started with.</param>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="StandardOutput">Captured standard output.</param>
/// <param name="StandardError">Captured standard error.</param>
public sealed record NpmCliResult(IReadOnlyList<string> Arguments, int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>
    /// Whether the process reported success.
    /// </summary>
    public bool Succeeded => ExitCode == 0;

    /// <summary>
    /// Whether the process was killed for outrunning its timeout rather than reporting a result.
    /// </summary>
    public bool TimedOut { get; init; }

    /// <summary>
    /// Returns the command and its output, for an error message that can be acted on.
    /// </summary>
    public string Describe()
    {
        List<string> lines = [$"npm {string.Join(" ", Arguments)} exited with {ExitCode}."];

        if (!string.IsNullOrWhiteSpace(StandardOutput))
            lines.Add(StandardOutput.Trim());

        if (!string.IsNullOrWhiteSpace(StandardError))
            lines.Add(StandardError.Trim());

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Runs the <c>npm</c> command line on the test host.
/// </summary>
/// <remarks>
/// On Windows npm is a cmd file, and with <c>UseShellExecute</c> off the PATHEXT search does not
/// run, so the file name has to say <c>npm.cmd</c> itself. Passing arguments to a cmd file makes
/// .NET apply its cmd escaping guard; every argument this class is handed is either a fixed verb or
/// a script name validated to a safe character class, so the guard never trips.
/// </remarks>
public static class NpmCli
{
    /// <summary>
    /// Runs <c>npm</c> and captures its output.
    /// </summary>
    /// <param name="arguments">The arguments, one element per argument.</param>
    /// <param name="workingDirectory">The working directory.</param>
    /// <param name="timeout">How long the command may run. Use <see cref="Timeout.InfiniteTimeSpan"/> for no limit.</param>
    /// <param name="cancellationToken">The cancellation token for the running command.</param>
    /// <remarks>
    /// A command that outruns its timeout is killed, process tree and all, and comes back as a
    /// failed result rather than an exception, so a caller that can recover still gets the chance to.
    /// </remarks>
    public static async Task<NpmCliResult> RunAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        ProcessStartInfo startInfo = new()
        {
            FileName = ResolveNpmExecutable(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        // Progress bars and prompts are for terminals. A captured build wants plain lines and no
        // question it cannot answer.
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["CI"] = "true";

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
            return new NpmCliResult(
                [.. arguments],
                -1,
                string.Empty,
                $"'npm {string.Join(" ", arguments)}' did not finish within {timeout:g} and was terminated.")
            {
                TimedOut = true,
            };
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            throw;
        }

        return new NpmCliResult(
            [.. arguments],
            process.ExitCode,
            await ReadOrEmptyAsync(standardOutput).ConfigureAwait(false),
            await ReadOrEmptyAsync(standardError).ConfigureAwait(false));
    }

    /// <summary>
    /// Resolves the npm executable to its absolute path.
    /// </summary>
    /// <remarks>
    /// A bare <c>npm.cmd</c> would run, but wrongly: cmd expands the script's own <c>%~dp0</c>
    /// against the working directory when the name it was invoked by carries no path, so npm's
    /// launcher looks for its internals inside the project being built and fails. An absolute path
    /// makes the expansion land where the script actually lives.
    /// </remarks>
    private static string ResolveNpmExecutable()
    {
        if (!OperatingSystem.IsWindows())
            return "npm";

        string[] pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string directory in pathDirectories)
        {
            string candidate = System.IO.Path.Combine(directory, "npm.cmd");
            if (System.IO.File.Exists(candidate))
                return candidate;
        }

        throw new TestFramework.Core.Exceptions.FrameworkConfigurationException(
            "No 'npm.cmd' was found on the PATH, so the site cannot be built on this host.",
            ["Install node and npm, or let the source build inside a container with BuiltInContainer()."]);
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
}
