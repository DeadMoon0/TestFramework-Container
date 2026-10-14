using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Testcontainers.Containers;
using TestFramework.Azure;
using TestFramework.Azure.Extensions;
using TestFramework.Config;
using TestFramework.Core.Stages;
using TestFramework.Core.Timelines;
using Xunit;

namespace TestFramework.Container.Azure.Tests;

[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class StartFailureCleanupCollection
{
    public const string CollectionName = "Start failure cleanup";
}

/// <summary>
/// A container that came up and then never became ready is removed, not left running.
/// </summary>
/// <remarks>
/// A component tears down only what it returned, and one whose readiness wait failed returns nothing - so before
/// starts were owned until handed over, the SQL Server here kept running until the test host exited. Serial,
/// because it compares the daemon's containers before and after, and a sibling starting SQL Server would read as a
/// leak.
/// </remarks>
[Trait("Category", "DockerSmoke")]
[Collection(StartFailureCleanupCollection.CollectionName)]
public sealed class StartFailureCleanupTests
{
    /// <summary>A readiness window no SQL Server can meet, so the wait fails after the container started.</summary>
    private sealed class NoPatience : DockerAzureInfrastructureDefinition
    {
        public override TimeSpan? MsSqlReadinessTimeout => TimeSpan.FromMilliseconds(1);
    }

    [Fact]
    public async Task ASqlServerThatNeverBecomesReadyIsRemoved()
    {
        IReadOnlyList<string> before = await SqlServerContainersAsync();

        TimelineRun run = await Timeline.Create()
            .Trigger(AzureExt.Trigger.IsLive.Sql("sql")).Name("live")
            .Build()
            .SetupRun(ConfigInstance.Create()
                .LoadDockerAzureConfig()
                .OverrideConfig(new Dictionary<string, string?>
                {
                    ["SqlDatabase:sql:ConnectionString"] = "Server=localhost;Database=master;User Id=sa;Password=unused;TrustServerCertificate=True",
                    ["SqlDatabase:sql:DatabaseName"] = "master",
                })
                .Build())
            .SetEnv(new DockerAzureEnvironment().Include<NoPatience>())
            .RunAsync();

        // The environment really failed - otherwise this would pass without reaching the path it is about.
        Assert.Equal(StageState.Error, run.Stages.First().Result.State);

        IReadOnlyList<string> leaked = (await SqlServerContainersAsync()).Except(before).ToList();
        Assert.True(leaked.Count == 0, $"SQL Server container(s) left running after the failed start: {string.Join(", ", leaked)}");
    }

    private static async Task<IReadOnlyList<string>> SqlServerContainersAsync()
    {
        // Only this test process's containers: both target frameworks run as parallel processes.
        ContainerDockerCommands.CommandResult result = await ContainerDockerCommands.RunAsync(
            $"ps -aq --no-trunc --filter ancestor={DockerAzureDefaults.MsSqlImage} --filter label=org.testcontainers.session-id={ResourceReaper.DefaultSessionId}",
            CancellationToken.None);
        Assert.True(result.ExitCode == 0, $"Listing containers failed: {result.StandardError}");

        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
