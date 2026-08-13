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
        if (!OperatingSystem.IsWindows())
            return null;

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DockerHostVariable)))
            return null;

        foreach (string candidate in CandidateHosts)
        {
            if (!NamedPipeExists(candidate))
                continue;

            Environment.SetEnvironmentVariable(DockerHostVariable, candidate);
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
