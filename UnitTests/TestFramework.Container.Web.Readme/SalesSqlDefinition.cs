using TestFramework.Web.Sql;

namespace TestFramework.Container.Web.Readme;

/// <summary>The database the application samples point at, which the README names but does not show.</summary>
public sealed class SalesSqlDefinition : DockerSqlDefinition
{
    public override SqlIdentifier Identifier => "sales";

    protected override void Configure(DockerSqlBuilder builder) => builder
        .WithDatabase("SalesDb")
        .WithSchemaFromModels<Order>();
}
