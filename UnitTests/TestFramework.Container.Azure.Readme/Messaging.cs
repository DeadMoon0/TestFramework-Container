using TestFramework.Azure.Identifier;
using TestFramework.Container.Sources;

namespace TestFramework.Container.Azure.Readme.Messaging;

#region snippet:topology
public sealed class ProcessingReply : DockerServiceBusDefinition
{
    public override ServiceBusIdentifier Identifier => "ProcessingReply";

    protected override void ConfigureServiceBusTopology(DockerServiceBusTopologyBuilder builder)
    {
        builder.AddNamespace("sbemulatorns", ns => ns
            .AddTopic("processing-reply", topic => topic.AddSubscription("Default")));
    }
}
#endregion

#region snippet:queue-topic-subscription
public sealed class SharedMessaging : DockerServiceBusDefinition
{
    public override ServiceBusIdentifier Identifier => "messaging";

    public DockerServiceBusEndpoint Incoming => DockerServiceBusEndpoint.TopicSubscription("orders", "processor");

    public DockerServiceBusEndpoint Reply => DockerServiceBusEndpoint.TopicSubscription("orders-reply", "default");

    protected override void ConfigureServiceBusTopology(DockerServiceBusTopologyBuilder builder)
    {
        builder.AddNamespace("sbemulatorns", ns => ns
            .AddQueue("audit-trail")
            .AddTopic("orders", topic => topic
                .AddSubscription("processor")
                .AddSubscription("dead-letter-review"))
            .AddTopic("orders-reply", topic => topic
                .AddSubscription("default")));
    }
}
#endregion

public sealed class MessagingApp : DockerFunctionAppDefinition
{
    public override FunctionAppIdentifier Identifier => "messaging-app";

    public override ContainerSource Source => ContainerSource.Project("../Processor/Processor.csproj").BuiltOnHost();

    protected override void Configure(DockerFunctionAppBuilder builder)
    {
        #region snippet:endpoint-selection
        builder.UseServiceBusTrigger<SharedMessaging>(bus => bus.Incoming);
        builder.UseServiceBusReply<SharedMessaging>(bus => bus.Reply);
        #endregion
    }
}

#region snippet:orders-bus
public sealed class OrdersBus : DockerServiceBusDefinition
{
    public override ServiceBusIdentifier Identifier => "orders-bus";

    protected override void ConfigureServiceBusTopology(DockerServiceBusTopologyBuilder builder)
    {
        builder.AddNamespace("sbemulatorns", ns => ns
            .AddTopic("orders", topic => topic.AddSubscription("processor"))
            .AddTopic("orders-reply", topic => topic.AddSubscription("default")));
    }
}
#endregion
