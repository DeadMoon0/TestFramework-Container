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

    [Fact]
    public void EnsureConfigured_LeavesAnExplicitlyConfiguredHostAlone()
    {
        string? original = Environment.GetEnvironmentVariable("DOCKER_HOST");
        try
        {
            Environment.SetEnvironmentVariable("DOCKER_HOST", "tcp://explicit:2375");

            Assert.Null(ContainerDockerHost.EnsureConfigured());
            Assert.Equal("tcp://explicit:2375", Environment.GetEnvironmentVariable("DOCKER_HOST"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOCKER_HOST", original);
        }
    }
}
