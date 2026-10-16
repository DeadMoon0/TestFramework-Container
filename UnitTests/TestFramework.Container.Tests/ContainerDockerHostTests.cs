using System;
using Xunit;

namespace TestFramework.Container.Tests;

/// <summary>
/// Covers how a Docker Desktop named pipe is recognised, which decides whether a Windows machine
/// can reach its engine at all.
/// </summary>
public class ContainerDockerHostTests
{
    [Fact]
    public void NamedPipeExists_RejectsAnAddressThatIsNotANamedPipe()
    {
        Assert.False(ContainerDockerHost.NamedPipeExists("tcp://localhost:2375"));
        Assert.False(ContainerDockerHost.NamedPipeExists("unix:///var/run/docker.sock"));
    }

    [Fact]
    public void NamedPipeExists_ReportsAPipeThatIsNotThere()
        => Assert.False(ContainerDockerHost.NamedPipeExists("npipe://./pipe/testframework_absent_engine"));

    [Fact]
    public void CandidateHosts_CoverTheTwoPipesDockerDesktopUses()
    {
        Assert.Contains("npipe://./pipe/docker_engine", ContainerDockerHost.CandidateHosts);
        Assert.Contains("npipe://./pipe/dockerDesktopLinuxEngine", ContainerDockerHost.CandidateHosts);
    }

    // The decision is tested without DOCKER_HOST: the variable is process-wide, and setting it here made a
    // container test running in parallel try to reach the fake address.
    [Fact]
    public void AnExplicitlyConfiguredHostIsLeftAlone()
        => Assert.Null(ContainerDockerHost.HostToConfigure("tcp://explicit:2375", onWindows: true, pipeExists: _ => true));

    [Fact]
    public void WithNothingConfigured_TheFirstDockerDesktopPipeThatExistsIsChosen()
        => Assert.Equal(
            "npipe://./pipe/dockerDesktopLinuxEngine",
            ContainerDockerHost.HostToConfigure(null, onWindows: true, pipeExists: pipe => pipe.EndsWith("dockerDesktopLinuxEngine", StringComparison.Ordinal)));

    [Fact]
    public void OffWindows_NoPipeIsChosen()
        => Assert.Null(ContainerDockerHost.HostToConfigure(null, onWindows: false, pipeExists: _ => true));
}
