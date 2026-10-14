using TestFramework.Azure.Identifier;
using TestFramework.Container.Azure.Contracts;
using TestFramework.Container.Sources;

namespace TestFramework.Container.Azure.Readme.Contracts;

#region snippet:contracts
public sealed class ReplyBus : DockerServiceBusDefinition
{
    public override ServiceBusIdentifier Identifier => "bus";

    protected override void ConfigureContracts(DockerAzureContractBuilder contracts)
    {
        contracts.Provide(new ServiceBusEndpointContract
        {
            ContractKey = "reply",
            ServiceBusIdentifier = Identifier,
            EndpointKind = ServiceBusEndpointKind.Queue,
            EntityName = "processing-reply",
        });
    }
}

public sealed class ReplyConsumerFunctionApp : DockerFunctionAppDefinition
{
    public override FunctionAppIdentifier Identifier => "func";

    public override ContainerSource Source =>
        ContainerSource.Project("../ReplyConsumer/ReplyConsumer.csproj").BuiltOnHost();

    protected override void ConfigureDependencies(DockerAzureDependencyBuilder dependencies)
    {
        dependencies.Include<ReplyBus>();
    }

    protected override void ConfigureContracts(DockerAzureContractBuilder contracts)
    {
        contracts.Require(new ServiceBusEndpointContract
        {
            ContractKey = "reply",
            ServiceBusIdentifier = "bus",
            EndpointKind = ServiceBusEndpointKind.Queue,
            EntityName = "processing-reply",
        });
    }
}
#endregion
