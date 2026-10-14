using TestFramework.Azure.Identifier;
using TestFramework.Container.Sources;

namespace TestFramework.Container.Azure.Readme.Bindings;

public sealed class OrdersStorage : DockerStorageDefinition
{
    public override StorageAccountIdentifier Identifier => "orders";

    protected override string? TableContainerName => "orders";
}

public sealed class OrdersApp : DockerFunctionAppDefinition
{
    public override FunctionAppIdentifier Identifier => "orders";

    public override ContainerSource Source => ContainerSource.Project("../Orders/Orders.csproj").BuiltOnHost();

    protected override void Configure(DockerFunctionAppBuilder builder)
    {
        #region snippet:storage-binding
        builder.UseStorage<OrdersStorage>(new FunctionAppStorageBinding
        {
            TableNameSettingName = "OrdersTable",
        });
        #endregion
    }
}
