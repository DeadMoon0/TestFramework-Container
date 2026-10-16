using System;
using System.Collections.Generic;
using System.IO;

namespace TestFramework.Container;

/// <summary>
/// Points the Docker client at the engine a Windows machine actually runs.
/// </summary>
/// <remarks>
/// Docker Desktop on Windows exposes the engine over a named pipe whose name differs between
/// installations, and the client library does not probe for it. Without this, a machine with a
/// working Docker installation fails to connect for no visible reason.
/// </remarks>
public static class ContainerDockerHost
{
    private const string DockerHostVariable = "DOCKER_HOST";
    private const string NamedPipePrefix = "npipe://./pipe/";

    /// <summary>
    /// The named pipes Docker Desktop is known to listen on, in the order they are tried.
    /// </summary>
    public static IReadOnlyList<string> CandidateHosts { get; } =
    [
        "npipe://./pipe/docker_engine",
        "npipe://./pipe/dockerDesktopLinuxEngine",
    ];

    /// <summary>
    /// Sets <c>DOCKER_HOST</c> to the first candidate pipe that exists, unless it is already set.
    /// </summary>
    /// <returns>The value that was set, or <see langword="null"/> when nothing was changed.</returns>
    public static string? EnsureConfigured()
    {
        string? chosen = HostToConfigure(Environment.GetEnvironmentVariable(DockerHostVariable), OperatingSystem.IsWindows(), NamedPipeExists);

        if (chosen is not null)
            Environment.SetEnvironmentVariable(DockerHostVariable, chosen);

        return chosen;
    }

    /// <summary>
    /// Decides which host to point the process at, without reading or writing the environment.
    /// </summary>
    /// <remarks>
    /// Apart from <see cref="EnsureConfigured"/> so the decision can be tested without setting <c>DOCKER_HOST</c>
    /// for the whole test process, which every other test running beside it would then read.
    /// </remarks>
    /// <param name="configured">The <c>DOCKER_HOST</c> already set, if any.</param>
    /// <param name="onWindows">Whether this is Windows, the only platform with named-pipe hosts.</param>
    /// <param name="pipeExists">Whether a named-pipe address exists.</param>
    /// <returns>The host to set, or null when nothing should change.</returns>
    internal static string? HostToConfigure(string? configured, bool onWindows, Func<string, bool> pipeExists)
    {
        if (!onWindows || !string.IsNullOrWhiteSpace(configured))
            return null;

        foreach (string candidate in CandidateHosts)
        {
            if (pipeExists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Returns whether the named pipe behind a <c>npipe://</c> address exists.
    /// </summary>
    /// <param name="dockerHost">The address to check.</param>
    public static bool NamedPipeExists(string dockerHost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dockerHost);

        if (!dockerHost.StartsWith(NamedPipePrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        return File.Exists($@"\\.\pipe\{dockerHost[NamedPipePrefix.Length..]}");
    }
}
