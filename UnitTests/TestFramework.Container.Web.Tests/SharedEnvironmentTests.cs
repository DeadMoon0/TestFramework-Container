using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DotNet.Testcontainers.Networks;
using TestFramework.Config;
using TestFramework.Core.Environment.Graph;
using TestFramework.Core.Steps;
using TestFramework.Core.Steps.Options;
using TestFramework.Core.Timelines;
using TestFramework.Web;
using TestFramework.Web.Extensions;
using TestFramework.Web.Sql;
using TestFramework.Web.Sql.Steps.IsLive;
using Xunit;

namespace TestFramework.Container.Web.Tests;

/// <summary>
/// Two runs sharing one environment instance each build on what they started themselves.
/// </summary>
/// <remarks>
/// The environment used to keep every component's live state in a table on the instance - the only way a component
/// could reach a sibling's - so two runs sharing an instance wrote over each other: one run's SQL Server joined the
/// other run's network. The proof reads, while each run is live, which networks its SQL Server container is on, and
/// compares that with the network the same run created.
/// </remarks>
[Trait("Category", "DockerSmoke")]
[Collection(SeveralSqlServersCollection.CollectionName)]
public class SharedEnvironmentTests
{
    private sealed class SharedSqlDefinition : DockerSqlDefinition
    {
        public override SqlIdentifier Identifier => "main";

        protected override void Configure(DockerSqlBuilder builder) => builder.WithDatabase("SharedDb");
    }

    [Fact]
    public async Task TwoRunsOnOneEnvironment_EachJoinTheirOwnNetwork()
    {
        DockerWebEnvironment shared = DockerWebEnvironment.For<SharedSqlDefinition>();

        async Task<TimelineRun> Run() => await Timeline.Create()
            .Trigger(WebExt.Sql.IsLive("main", SqlAlivenessLevel.Reachable)).Label("live")
            .Trigger(new RecordsSqlNetworks()).Label("networks")
            .Build()
            .SetupRun(ConfigInstance.Create().LoadWebConfig().Build())
            .SetEnv(shared)
            .RunAsync();

        TimelineRun[] runs = await Task.WhenAll(Run(), Run());

        foreach (TimelineRun run in runs)
        {
            run.EnsureRanToCompletion();
            string own = run.EnvironmentContext.GetState<INetwork>(DockerWebEnvironment.NetworkComponentId)!.Name;
            string joined = run.VariableStore.GetVariable<string>("sql-networks")!;

            Assert.Contains(own, joined, StringComparison.Ordinal);
        }
    }

    /// <summary>Asks Docker, while the run is live, which networks the run's SQL Server container is on.</summary>
    private sealed class RecordsSqlNetworks : Step<EmptyStepResultContext>
    {
        public override string Name => "Records the SQL Server's networks";

        public override string Description => "Reads the networks of this run's SQL Server container from Docker.";

        public override bool DoesReturn => false;

        public override Step<EmptyStepResultContext> Clone() => new RecordsSqlNetworks().WithClonedOptions(this);

        public override StepInstance<Step<EmptyStepResultContext>, EmptyStepResultContext> GetInstance() => new(this);

        public override void DeclareIO(StepIOContract contract) => contract.Output(new TestFramework.Core.Variables.VariableIdentifier("sql-networks"), typeof(string));

        public override async Task<EmptyStepResultContext?> Execute(RunContext context)
        {
            string connection = context.Values.Require(
                ValueRef.For(WebResourceKinds.SqlKind.Name, "main", ValueNames.ConnectionString),
                ResourceVantage.Host);
            string port = Regex.Match(connection, @",(\d+)").Groups[1].Value;

            string listing = await Docker($"ps --no-trunc --filter ancestor={DockerWebDefaults.MsSqlImage} --format \"{{{{.ID}}}} {{{{.Ports}}}}\"");
            string container = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Single(line => line.Contains($":{port}->", StringComparison.Ordinal))
                .Split(' ')[0];
            string networks = await Docker($"inspect -f \"{{{{range $name, $_ := .NetworkSettings.Networks}}}}{{{{$name}}}} {{{{end}}}}\" {container}");

            context.Variables.SetVariable("sql-networks", networks.Trim());
            return EmptyStepResultContext.Instance;
        }

        private static async Task<string> Docker(string arguments)
        {
            // Through the package's own runner, which hands the CLI a DOCKER_HOST it can parse.
            ContainerDockerCommands.CommandResult result = await ContainerDockerCommands.RunAsync(arguments, System.Threading.CancellationToken.None);
            Assert.True(result.ExitCode == 0, $"docker {arguments} failed: {result.StandardError}");
            return result.StandardOutput;
        }
    }
}
