using TestFramework.Web.Sql.Steps;
using TestFramework.Web.Sql;

namespace TestFramework.Container.Web.Readme.Schema;

internal sealed class ScriptedSqlDefinition : DockerSqlDefinition
{
    public override SqlIdentifier Identifier => "main";

    protected override void Configure(DockerSqlBuilder builder)
    {
        builder.WithDatabase("SampleDb");

        #region snippet:schema-scripts
        builder.WithSchemaFromModels<Order>()
               .WithSchemaScript(SqlScript.FromFile("Schema/views.sql"))
               .WithSchemaScript(SqlScript.FromFile("Schema/reference-data.sql"));
        #endregion
    }
}
