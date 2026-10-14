using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Core.Exceptions;
using Xunit;

namespace TestFramework.Container.Tests;

/// <summary>
/// Docker that cannot serve this package's containers is refused once, up front, saying which of three things is
/// wrong.
/// </summary>
public class ContainerRuntimeTests
{
    [Fact]
    public async Task EnsureReady_IsSafeToCallRepeatedly()
    {
        // Both network components call it, and a run may create several environments.
        await ContainerRuntime.EnsureReadyAsync(CancellationToken.None);
        await ContainerRuntime.EnsureReadyAsync(null, CancellationToken.None);
    }

    [Fact]
    public void ADaemonServingLinuxContainers_IsAccepted()
        => Assert.Null(DockerProbe.Refusal(new ContainerDockerCommands.CommandResult(0, "linux\n", string.Empty), "the default Docker endpoint"));

    [Fact]
    public void AStoppedDaemon_IsRefusedNamingWhereItWasLookedFor()
    {
        // A failed probe used to pass: the old check returned early on a non-zero exit code.
        FrameworkConfigurationException refusal = Assert.IsType<FrameworkConfigurationException>(DockerProbe.Refusal(
            new ContainerDockerCommands.CommandResult(1, string.Empty, "Cannot connect to the Docker daemon. Is the docker daemon running?"),
            "'npipe:////./pipe/docker_engine'"));

        Assert.Contains("'npipe:////./pipe/docker_engine' did not answer", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Is the docker daemon running?", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Start Docker Desktop", refusal.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ADaemonThatDidNotAnswerInTime_IsRefusedAsNotAnswering()
    {
        FrameworkConfigurationException refusal = Assert.IsType<FrameworkConfigurationException>(DockerProbe.Refusal(
            new ContainerDockerCommands.CommandResult(-1, string.Empty, "'docker version' did not finish within 0:00:10 and was terminated."),
            "the default Docker endpoint"));

        Assert.Contains("the default Docker endpoint did not answer", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADaemonServingWindowsContainers_IsRefusedBeforeAnImageFailsToStart()
    {
        FrameworkConfigurationException refusal = Assert.IsType<FrameworkConfigurationException>(DockerProbe.Refusal(
            new ContainerDockerCommands.CommandResult(0, "windows\n", string.Empty),
            "the default Docker endpoint"));

        Assert.Contains("serves 'windows' containers", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Switch Docker Desktop to Linux containers.", refusal.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingExecutable_IsRefusedByNameRatherThanAsAWin32Exception()
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo($"no-such-docker-{Guid.NewGuid():N}") { UseShellExecute = false },
        };

        FrameworkConfigurationException refusal = Assert.Throws<FrameworkConfigurationException>(() => ContainerDockerCommands.Start(process));

        Assert.Contains("No 'docker' executable was found", refusal.Message, StringComparison.Ordinal);
        Assert.IsType<System.ComponentModel.Win32Exception>(refusal.InnerException);
    }
}
