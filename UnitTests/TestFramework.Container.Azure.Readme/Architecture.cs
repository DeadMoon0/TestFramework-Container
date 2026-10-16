using System.Threading.Tasks;
using TestFramework.Azure.Identifier;
using TestFramework.Config;
using TestFramework.Container.Azure.Contracts;
using TestFramework.Container.Sources;
using TestFramework.Core.Timelines;

namespace TestFramework.Container.Azure.Readme.Architecture;

public sealed record ProfileDocument(string Id, string PartitionKey);

#region snippet:arch-definitions
public sealed class MainStorage : DockerStorageAccountDefinition
{
    public override StorageAccountIdentifier Identifier => "MainStorage";
}

public sealed class MainDb : DockerCosmosDbDefinition<ProfileDocument>
{
    public override CosmosDbIdentifier Identifier => "MainDb";
}

public sealed class MainBus : DockerServiceBusDefinition
{
    public override ServiceBusIdentifier Identifier => "ProcessingReply";

    public DockerServiceBusEndpoint Reply => DockerServiceBusEndpoint.TopicSubscription("processing-reply", "Default");
}
#endregion

#region snippet:arch-function-app
public sealed class DefaultFunctionApp : DockerFunctionAppDefinition
{
    public override FunctionAppIdentifier Identifier => "Default";

    public override ContainerSource Source =>
        ContainerSource.Project("../AnalysisProcessor/AnalysisProcessor.csproj").BuiltOnHost();

    protected override void Configure(DockerFunctionAppBuilder builder)
    {
        builder
            .UseStorageAccount<MainStorage>()
            .UseCosmosDb<MainDb>()
            .UseServiceBusReply<MainBus>(bus => bus.Reply)
            .WithAppSetting("FeatureFlags__VerboseStartup", "true");
    }
}
#endregion

#region snippet:arch-contracts
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

public sealed class ReplyConsumer : DockerFunctionAppDefinition
{
    public override FunctionAppIdentifier Identifier => "func";

    public override ContainerSource Source =>
        ContainerSource.Project("../ReplyFunctions/ReplyFunctions.csproj").BuiltOnHost();

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

internal static class ArchitectureRun
{
    public static async Task<TimelineRun> Run(Timeline timeline, ConfigInstance config)
    {
        #region snippet:arch-composition
        DockerAzureEnvironment environment = new DockerAzureEnvironment()
            .Include<MainStorage>()
            .Include<MainDb>()
            .Include<MainBus>()
            .Include<DefaultFunctionApp>();

        TimelineRun run = await timeline
            .SetupRun(config)
            .SetEnv(environment)
            .RunAsync();
        #endregion
        return run;
    }
}
