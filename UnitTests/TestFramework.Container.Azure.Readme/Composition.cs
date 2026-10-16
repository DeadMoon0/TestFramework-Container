using System.Threading.Tasks;
using TestFramework.Azure.Identifier;
using TestFramework.Config;
using TestFramework.Container.Sources;
using TestFramework.Core.Timelines;

namespace TestFramework.Container.Azure.Readme.Composition;

public sealed record SampleDocument(string Id, string PartitionKey);

#region snippet:composition
public sealed class MainStorage : DockerStorageAccountDefinition
{
    public override StorageAccountIdentifier Identifier => "MainStorage";

    protected override string? BlobContainerName => "uploads";
}

public sealed class MainDb : DockerCosmosDbDefinition<SampleDocument>
{
    public override CosmosDbIdentifier Identifier => "MainDb";

    protected override string? DatabaseName => "AppDb";

    protected override string? ContainerName => "Documents";
}

public sealed class ProcessingReply : DockerServiceBusDefinition
{
    public override ServiceBusIdentifier Identifier => "ProcessingReply";

    public DockerServiceBusEndpoint Reply
        => DockerServiceBusEndpoint.TopicSubscription("processing-reply", "Default");

    protected override void ConfigureServiceBusTopology(DockerServiceBusTopologyBuilder builder)
    {
        builder.AddNamespace("sbemulatorns", ns => ns
            .AddTopic("processing-reply", topic => topic.AddSubscription("Default")));
    }
}

public sealed class DefaultFunctionApp : DockerFunctionAppDefinition
{
    public override FunctionAppIdentifier Identifier => "Default";

    // The payload is mounted into the Functions host, so a project source publishes on the host.
    public override ContainerSource Source =>
        ContainerSource.Project("../AnalysisProcessor/AnalysisProcessor.csproj").BuiltOnHost();

    protected override void Configure(DockerFunctionAppBuilder builder)
    {
        builder
            .UseStorageAccount<MainStorage>()
            .UseCosmosDb<MainDb>()
            .UseServiceBusReply<ProcessingReply>(bus => bus.Reply);
    }
}
#endregion

internal static class CompositionRun
{
    public static async Task<TimelineRun> Run(Timeline timeline, ConfigInstance config)
    {
        #region snippet:composition
        TimelineRun run = await timeline
            .SetupRun(config)
            .SetEnv(DockerAzureEnvironment.For<DefaultFunctionApp>())
            .RunAsync();
        #endregion
        return run;
    }
}
