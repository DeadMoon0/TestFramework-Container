using System.Threading.Tasks;
using TestFramework.Config;
using TestFramework.Core.Timelines;
using TestFramework.Core.Timelines.Assertions;
using TestFramework.Core.Variables;
using TestFramework.Web;
using TestFramework.Web.Extensions;
using TestFramework.Web.Sql;
using TestFramework.Web.Sql.Artifacts;

namespace TestFramework.Container.Web.Readme.Quickstart;

#region snippet:quickstart-definition
internal sealed class SampleSqlDefinition : DockerSqlDefinition
{
    public override SqlIdentifier Identifier => "main";

    protected override void Configure(DockerSqlBuilder builder) => builder
        .WithDatabase("SampleDb")
        .WithSchemaFromModels<Order, Customer>()
        .WithResetMode(SqlResetMode.RecreateDatabase);
}
#endregion

internal static class QuickstartRun
{
    public static async Task Run()
    {
        #region snippet:quickstart-run
        ConfigInstance config = ConfigInstance.Create()
            .LoadWebConfig()
            .AddWebSqlModels(models => models.For<Order>().Table("Orders").Key(x => x.Id).MaxLength(x => x.Name, 200))
            .Build();

        Timeline timeline = Timeline.Create()
            .SetupArtifact("order")
            .Trigger(WebExt.Sql.Scalar<int>("main", "SELECT COUNT(1) FROM [Orders]")).Label("count")
            .Build();

        TimelineRun run = await timeline.SetupRun(config)
            .SetEnv(DockerWebEnvironment.For<SampleSqlDefinition>())
            .AddArtifact("order",
                WebExt.Artifact.Sql.Row<Order>("main", Var.Const("1")),
                new SqlRowArtifactData<Order>(new Order { Id = 1, Name = "sample", Quantity = 3 }))
            .RunAsync();

        run.EnsureRanToCompletion();
        run.SqlScalar<int>("count").Should().Be(1);
        #endregion
    }
}
