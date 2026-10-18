using TestFramework.Web.Sql;

namespace TestFramework.Container.Web.Readme;

/// <summary>The database the hosted collection sample keeps warm.</summary>
public sealed class OrdersDatabase : DockerSqlDefinition
{
    public override SqlIdentifier Identifier => "orders";

    protected override void Configure(DockerSqlBuilder builder) => builder
        .WithDatabase("OrdersDb")
        .WithSchemaFromModels<Order>();
}
