using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Testcontainers.Containers;
using TestFramework.Config;
using TestFramework.Core.Stages;
using TestFramework.Core.Timelines;
using TestFramework.Web;
using TestFramework.Web.Extensions;
using TestFramework.Web.Sql;
using Xunit;

namespace TestFramework.Container.Web.Tests;

[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class StartFailureCleanupCollection
{
    public const string CollectionName = "Start failure cleanup";
}

/// <summary>
/// Pins that a container which came up and then failed before its component returned is removed.
/// </summary>
/// <remarks>
/// The damage it prevents is a container nobody owns. A component tears down only what it returned, so a
/// SQL Server that started and then failed on its schema used to keep running until the test host exited -
/// or for good, where the reaper is switched off. Serial, because it compares the daemon's containers
/// before and after, and a sibling test starting SQL Server beside it would read as a leak.
/// </remarks>
[Trait("Category", "DockerSmoke")]
[Collection(StartFailureCleanupCollection.CollectionName)]
public sealed class StartFailureCleanupTests
{
    /// <summary>A model with no key, so building its table fails once the server is already up.</summary>
    internal sealed class Keyless
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class KeylessSqlDefinition : DockerSqlDefinition
    {
        public override SqlIdentifier Identifier => "main";

        protected override void Configure(DockerSqlBuilder builder) => builder
            .WithDatabase("KeylessDb")
            .WithSchemaFromModels<Keyless>();
    }

    [Fact]
    public async Task ASqlServerThatFailsAfterStartingIsRemoved()
    {
        IReadOnlyList<string> before = await SqlServerContainersAsync();

        TimelineRun run = await Timeline.Create()
            .Trigger(WebExt.Sql.Execute("main", "SELECT 1")).Name("query")
            .Build()
            .SetupRun(ConfigInstance.Create().LoadWebConfig().Build())
            .SetEnv(DockerWebEnvironment.For<KeylessSqlDefinition>())
            .RunAsync();

        // The environment really did fail - otherwise the test would pass without ever reaching the path
        // it is about. The Pre-Setup stage is where the environment is built.
        Assert.Equal(StageState.Error, run.Stages.First().Result.State);

        IReadOnlyList<string> leaked = (await SqlServerContainersAsync()).Except(before).ToList();
        Assert.True(leaked.Count == 0, $"SQL Server container(s) left running after the failed start: {string.Join(", ", leaked)}");
    }

    private static async Task<IReadOnlyList<string>> SqlServerContainersAsync()
    {
        // Through the package's own runner: the environment rewrites DOCKER_HOST into a form the docker
        // CLI cannot parse, and the runner is what translates it back. A plain process here listed nothing
        // after the first environment had started, and the test passed while the container kept running.
        // Only this test process's containers: both target frameworks run as parallel processes, and the
        // other one starting SQL Server would otherwise read as this one's leak.
        ContainerDockerCommands.CommandResult result = await ContainerDockerCommands.RunAsync(
            $"ps -aq --no-trunc --filter ancestor={DockerWebDefaults.MsSqlImage} --filter label=org.testcontainers.session-id={ResourceReaper.DefaultSessionId}",
            CancellationToken.None);
        Assert.True(result.ExitCode == 0, $"Listing containers failed: {result.StandardError}");

        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
