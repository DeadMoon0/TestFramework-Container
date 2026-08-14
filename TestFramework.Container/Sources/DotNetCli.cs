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
    /// Runs <c>dotnet</c> and captures its output.
    /// </summary>
    /// <param name="arguments">The arguments, one element per argument.</param>
    /// <param name="workingDirectory">The working directory, or <see langword="null"/> for the current one.</param>
    /// <param name="cancellationToken">The cancellation token for the running command.</param>
    public static async Task<DotNetCliResult> RunAsync(IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken cancellationToken)
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

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return new DotNetCliResult(
            [.. arguments],
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
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
