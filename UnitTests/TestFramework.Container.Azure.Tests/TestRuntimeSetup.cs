using System.Runtime.CompilerServices;

namespace TestFramework.Container.Azure.Tests;

internal static class TestRuntimeSetup
{
    private const string DockerHostEnvironmentVariable = "DOCKER_HOST";

    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DockerHostEnvironmentVariable)))
        {
            return;
        }

        foreach (string candidate in GetCandidateDockerHosts())
        {
            if (NamedPipeExists(candidate))
            {
                Environment.SetEnvironmentVariable(DockerHostEnvironmentVariable, candidate);
                return;
            }
        }
    }

    private static IReadOnlyList<string> GetCandidateDockerHosts()
    {
        return
        [
            "npipe://./pipe/docker_engine",
            "npipe://./pipe/dockerDesktopLinuxEngine",
        ];
    }

    private static bool NamedPipeExists(string dockerHost)
    {
        const string prefix = "npipe://./pipe/";
        if (!dockerHost.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string pipeName = dockerHost[prefix.Length..];
        string pipePath = $@"\\.\pipe\{pipeName}";
        return File.Exists(pipePath);
    }
}