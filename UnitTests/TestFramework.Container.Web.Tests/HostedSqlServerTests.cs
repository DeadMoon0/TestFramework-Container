using System;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using TestFramework.Config;
using TestFramework.Core.Environment.Graph;
using TestFramework.Core.Steps;
using TestFramework.Core.Steps.Options;
using TestFramework.Core.Timelines;
using TestFramework.Core.Variables;
using TestFramework.Web;
using TestFramework.Web.Extensions;
using TestFramework.Web.Sql;
using TestFramework.Web.Sql.Steps;
using Xunit;

namespace TestFramework.Container.Web.Tests;

/// <summary>
/// A SQL Server that outlives runs still gives every run the database its reset mode promises.
/// </summary>
/// <remarks>
/// Each case boots its own stack, runs twice on it - the first run leaves a row behind, the second counts what it
/// finds - and checks the second run was handed the very server the first one used.
/// </remarks>
[Trait("Category", "DockerSmoke")]
[Collection(SeveralSqlServersCollection.CollectionName)]
public class HostedSqlServerTests
{
    private const string MarkerTable = "CREATE TABLE dbo.Marker (Id INT NOT NULL);";

    [Fact]
    public async Task RecreateDatabase_StartsTheNextRunFromTheDeclaredSchema()
        => Assert.Equal(0, await RowsTheSecondRunFinds<RecreatingState>());

    [Fact]
    public async Task RunResetScript_ClearsWhatThePreviousRunLeft()
        => Assert.Equal(0, await RowsTheSecondRunFinds<ResetScriptState>());

    [Fact]
    public async Task NoReset_KeepsWhatThePreviousRunLeft()
        => Assert.Equal(1, await RowsTheSecondRunFinds<KeepingState>());

    private static async Task<int> RowsTheSecondRunFinds<TState>()
        where TState : IDockerWebHostedFixtureState, new()
    {
        DockerWebHostedCollectionFixture<TState> fixture = new();
        await fixture.InitializeAsync();

        try
        {
            TimelineRun first = await RunSql(fixture.GetEnv(), "INSERT INTO dbo.Marker (Id) VALUES (1); SELECT COUNT(*) FROM dbo.Marker;");
            TimelineRun second = await RunSql(fixture.GetEnv(), "SELECT COUNT(*) FROM dbo.Marker;");

            first.EnsureRanToCompletion();
            second.EnsureRanToCompletion();

            Assert.Equal(
                first.EnvironmentContext.GetState<SqlServerComponentState>(DockerWebEnvironment.SqlServerComponentId)!.Container.Id,
                second.EnvironmentContext.GetState<SqlServerComponentState>(DockerWebEnvironment.SqlServerComponentId)!.Container.Id);

            return second.VariableStore.GetVariable<int>("rows");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static Task<TimelineRun> RunSql(Core.Environment.IEnvironmentProvider environment, string sql)
        => Timeline.Create()
            .Trigger(new CountsRows(sql)).Name("rows")
            .Build()
            .SetupRun(ConfigInstance.Create().LoadWebConfig().Build())
            .SetEnv(environment)
            .RunAsync();

    private abstract class MarkerState : IDockerWebHostedFixtureState
    {
        public DockerWebEnvironment CreateEnvironment() => new DockerWebEnvironment().Include(Definition());

        public ConfigInstance CreatePersistentConfig() => ConfigInstance.Create().LoadWebConfig().Build();

        protected abstract DockerSqlDefinition Definition();
    }

    private sealed class RecreatingState : MarkerState
    {
        protected override DockerSqlDefinition Definition() => new MarkerDefinition(SqlResetMode.RecreateDatabase);
    }

    private sealed class ResetScriptState : MarkerState
    {
        protected override DockerSqlDefinition Definition() => new MarkerDefinition(SqlResetMode.RunResetScript);
    }

    private sealed class KeepingState : MarkerState
    {
        protected override DockerSqlDefinition Definition() => new MarkerDefinition(SqlResetMode.None);
    }

    private sealed class MarkerDefinition(SqlResetMode resetMode) : DockerSqlDefinition
    {
        public override SqlIdentifier Identifier => "main";

        protected override void Configure(DockerSqlBuilder builder)
        {
            builder.WithDatabase("MarkerDb")
                .WithSchemaScript(SqlScript.FromText(MarkerTable, "marker table"))
                .WithResetMode(resetMode);

            if (resetMode == SqlResetMode.RunResetScript)
                builder.WithResetScript(SqlScript.FromText("DELETE FROM dbo.Marker;", "clear markers"));
        }
    }

    /// <summary>Runs one statement against this run's database and records the number it returns.</summary>
    private sealed class CountsRows(string sql) : Step<EmptyStepResultContext>
    {
        public override string Name => "Counts rows";

        public override string Description => "Runs a statement against the run's database and records the count it returns.";

        public override bool DoesReturn => false;

        public override Step<EmptyStepResultContext> Clone() => new CountsRows(sql).WithClonedOptions(this);

        public override StepInstance<Step<EmptyStepResultContext>, EmptyStepResultContext> GetInstance() => new(this);

        public override void DeclareIO(StepIOContract contract) => contract.Output(new VariableIdentifier("rows"), typeof(int));

        public override async Task<EmptyStepResultContext?> Execute(RunContext context)
        {
            string connectionString = context.Values.Require(
                ValueRef.For(WebEnvironmentResourceKinds.SqlKind.Name, "main", ValueNames.ConnectionString),
                ResourceVantage.Host);

            await using SqlConnection connection = new(connectionString);
            await connection.OpenAsync(context.Deadline.Token);
            await using SqlCommand command = new(sql, connection);

            context.Variables.SetVariable("rows", Convert.ToInt32(await command.ExecuteScalarAsync(context.Deadline.Token)));
            return EmptyStepResultContext.Instance;
        }
    }
}
