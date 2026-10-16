using System;
using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Container.Azure.Contracts;

namespace TestFramework.Container.Azure;

/// <summary>
/// One Service Bus entity a Function App binds to: a queue, a topic, or a topic subscription.
/// </summary>
public sealed class DockerServiceBusEndpoint
{
    private DockerServiceBusEndpoint(ServiceBusEndpointKind kind, string entityName, string? subscriptionName)
    {
        if (string.IsNullOrWhiteSpace(entityName))
            throw new ArgumentException("A Service Bus endpoint name is required.", nameof(entityName));

        if (kind == ServiceBusEndpointKind.TopicSubscription && string.IsNullOrWhiteSpace(subscriptionName))
            throw new ArgumentException("A topic subscription endpoint requires a subscription name.", nameof(subscriptionName));

        if (kind != ServiceBusEndpointKind.TopicSubscription && subscriptionName is not null)
            throw new ArgumentException("Only topic subscription endpoints may declare a subscription name.", nameof(subscriptionName));

        Kind = kind;
        EntityName = entityName;
        SubscriptionName = subscriptionName;
    }

    /// <summary>
    /// Whether the endpoint is a queue, a topic or a topic subscription.
    /// </summary>
    public ServiceBusEndpointKind Kind { get; }

    /// <summary>
    /// The queue name, or the topic name for a topic or topic subscription.
    /// </summary>
    public string EntityName { get; }

    /// <summary>
    /// The subscription name; set only for a topic subscription.
    /// </summary>
    public string? SubscriptionName { get; }

    /// <summary>
    /// A queue endpoint.
    /// </summary>
    /// <param name="queueName">The queue name; must not be empty.</param>
    /// <returns>The endpoint.</returns>
    public static DockerServiceBusEndpoint Queue(string queueName) => new(ServiceBusEndpointKind.Queue, queueName, null);

    /// <summary>
    /// A topic endpoint.
    /// </summary>
    /// <param name="topicName">The topic name; must not be empty.</param>
    /// <returns>The endpoint.</returns>
    public static DockerServiceBusEndpoint Topic(string topicName) => new(ServiceBusEndpointKind.Topic, topicName, null);

    /// <summary>
    /// A topic subscription endpoint.
    /// </summary>
    /// <param name="topicName">The topic name; must not be empty.</param>
    /// <param name="subscriptionName">The subscription name; must not be empty.</param>
    /// <returns>The endpoint.</returns>
    public static DockerServiceBusEndpoint TopicSubscription(string topicName, string subscriptionName) => new(ServiceBusEndpointKind.TopicSubscription, topicName, subscriptionName);

    internal ServiceBusConfig CreateConfig(string connectionString, bool requiredSession) => new()
    {
        ConnectionString = connectionString,
        QueueName = Kind == ServiceBusEndpointKind.Queue ? EntityName : null,
        TopicName = Kind == ServiceBusEndpointKind.Queue ? null : EntityName,
        SubscriptionName = SubscriptionName,
        RequiredSession = requiredSession,
    };
}
