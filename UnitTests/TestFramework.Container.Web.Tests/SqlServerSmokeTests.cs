using System.Threading.Tasks;
using TestFramework.Config;
using TestFramework.Core.Timelines;
using TestFramework.Core.Timelines.Assertions;
using TestFramework.Core.Variables;
using TestFramework.Web;
using TestFramework.Web.Extensions;
using TestFramework.Web.Sql;
using TestFramework.Web.Sql.Artifacts;
using TestFramework.Web.Sql.Steps;
using TestFramework.Web.Sql.Steps.IsLive;
using Xunit;

namespace TestFramework.Container.Web.Tests;

/// <summary>
/// Proves the whole path against a real container: a database created from the models a test
/// declares, a row seeded into it, and a timeline that reads the row back.
/// </summary>
/// <remarks>
/// Needs a Docker daemon, so it is excluded from the default run. Run with
/// <c>--filter "Category=DockerSmoke"</c>.
/// </remarks>
[Trait("Category", "DockerSmoke")]
public class SqlServerSmokeTests
{
    internal sealed class SmokeOrder
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal? Total { get; set; }
    }

    private sealed class SmokeSqlDefinition : DockerSqlDefinition
    {
        public override SqlIdentifier Identifier => "main";

        protected override void Configure(DockerSqlBuilder builder) => builder
            .WithDatabase("SmokeDb")
            .WithSchemaFromModels<SmokeOrder>()
            .WithResetMode(SqlResetMode.RecreateDatabase);
    }

    private static ConfigInstance CreateConfig()
        => ConfigInstance.Create()
            .LoadWebConfig()
            .AddWebSqlModels(models => models.For<SmokeOrder>()
                .Table("Orders")
                .Key(x => x.Id)
                .MaxLength(x => x.Name, 200)
                .Precision(x => x.Total, 12, 2))
            .Build();

    [Fact]
    public async Task Timeline_ReadsBackARowSeededIntoAContainerProvisionedFromModels()
    {
        Timeline timeline = Timeline.Create()
            .Trigger(WebExt.Sql.IsLive("main", SqlAlivenessLevel.Database)).Name("live")
            .SetupArtifact("order")
            .Trigger(WebExt.Sql.Scalar<int>("main", "SELECT COUNT(1) FROM [Orders] WHERE [Name] = @name")
                .WithParameter("name", Var.Const("smoke"))).Name("count")
            .FindArtifact("found", WebExt.ArtifactFinder.Sql.Where<SmokeOrder>("main", "Quantity = @quantity")
                .WithParameter("quantity", Var.Const(3)))
            .Build();

        TimelineRun run = await timeline.SetupRun(CreateConfig())
            .SetEnv(DockerWebEnvironment.For<SmokeSqlDefinition>())
            .AddArtifact(
                "order",
                WebExt.Artifact.Sql.Row<SmokeOrder>("main", Var.Const("1")),
                new SqlRowArtifactData<SmokeOrder>(new SmokeOrder { Id = 1, Name = "smoke", Quantity = 3, Total = 12.34m }))
            .RunAsync();

        run.EnsureRanToCompletion();
        run.SqlProbe("live").Select(probe => probe.Success).Should().Be(true);
        run.SqlScalar<int>("count").Should().Be(1);
        run.SqlRow<SmokeOrder>("found").Select(order => order.Total).Should().Be(12.34m);
        Assert.True(run.EnvironmentContext.Contains(DockerWebEnvironment.SqlServerComponentId));
    }
}
