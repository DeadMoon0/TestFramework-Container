using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Core.Exceptions;
using TestFramework.Core.Logging;
using Testcontainers.ServiceBus;

namespace TestFramework.Container.Azure;

/// <summary>
/// Declares the Service Bus emulator topology (namespaces, queues, topics, subscriptions) in code instead of a config file.
/// </summary>
public sealed class DockerServiceBusTopologyBuilder
{
    private readonly List<ServiceBusEmulatorNamespaceDefinition> _namespaces = [];

    /// <summary>
    /// Adds an empty namespace.
    /// </summary>
    /// <remarks>Uses <c>null</c> for <c>configure</c>; a longer overload states it.</remarks>
    /// <param name="name">The namespace name; must not be empty.</param>
    /// <returns>This builder for chaining.</returns>
    public DockerServiceBusTopologyBuilder AddNamespace(string name)
        => this.AddNamespace(name, null);

    /// <summary>
    /// Adds a namespace and lets <paramref name="configure"/> declare its queues and topics.
    /// </summary>
    /// <param name="name">The namespace name; must not be empty.</param>
    /// <param name="configure">Declares the namespace's entities, or <c>null</c> for an empty namespace.</param>
    /// <returns>This builder for chaining.</returns>
    public DockerServiceBusTopologyBuilder AddNamespace(string name, Action<DockerServiceBusNamespaceBuilder>? configure)
    {
        DockerServiceBusNamespaceBuilder builder = new(name);
        configure?.Invoke(builder);
        _namespaces.Add(builder.Build());
        return this;
    }

    internal bool HasNamespaces => _namespaces.Count > 0;

    internal ServiceBusEmulatorTopologyDefinition Build()
    {
        if (_namespaces.Count == 0)
            throw new FrameworkConfigurationException("A Service Bus emulator topology must declare at least one namespace.");

        return new ServiceBusEmulatorTopologyDefinition(new ServiceBusEmulatorUserConfigDefinition([.. _namespaces], new ServiceBusEmulatorLoggingDefinition("Console")));
    }
}

/// <summary>
/// Declares the queues and topics of one Service Bus emulator namespace.
/// </summary>
public sealed class DockerServiceBusNamespaceBuilder
{
    private readonly string _name;
    private readonly List<ServiceBusEmulatorQueueDefinition> _queues = [];
    private readonly List<ServiceBusEmulatorTopicDefinition> _topics = [];

    internal DockerServiceBusNamespaceBuilder(string name)
    {
        _name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Namespace name must not be empty.", nameof(name))
            : name;
    }

    /// <summary>
    /// Adds a queue to the namespace.
    /// </summary>
    /// <param name="name">The queue name; must not be empty.</param>
    /// <returns>This builder for chaining.</returns>
    public DockerServiceBusNamespaceBuilder AddQueue(string name)
    {
        _queues.Add(new ServiceBusEmulatorQueueDefinition(RequireName(name, "Queue")));
        return this;
    }

    /// <summary>
    /// Adds a topic without subscriptions to the namespace.
    /// </summary>
    /// <remarks>Uses <c>null</c> for <c>configure</c>; a longer overload states it.</remarks>
    /// <param name="name">The topic name; must not be empty.</param>
    /// <returns>This builder for chaining.</returns>
    public DockerServiceBusNamespaceBuilder AddTopic(string name)
        => this.AddTopic(name, null);

    /// <summary>
    /// Adds a topic to the namespace and lets <paramref name="configure"/> declare its subscriptions.
    /// </summary>
    /// <param name="name">The topic name; must not be empty.</param>
    /// <param name="configure">Declares the topic's subscriptions, or <c>null</c> for none.</param>
    /// <returns>This builder for chaining.</returns>
    public DockerServiceBusNamespaceBuilder AddTopic(string name, Action<DockerServiceBusTopicBuilder>? configure)
    {
        DockerServiceBusTopicBuilder builder = new(name);
        configure?.Invoke(builder);
        _topics.Add(builder.Build());
        return this;
    }

    internal ServiceBusEmulatorNamespaceDefinition Build()
    {
        return new ServiceBusEmulatorNamespaceDefinition(_name, [.. _queues], [.. _topics]);
    }

    private static string RequireName(string name, string kind)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException($"{kind} name must not be empty.", nameof(name));

        return name;
    }
}

/// <summary>
/// Declares the subscriptions of one Service Bus emulator topic.
/// </summary>
public sealed class DockerServiceBusTopicBuilder
{
    private readonly string _name;
    private readonly List<ServiceBusEmulatorSubscriptionDefinition> _subscriptions = [];

    internal DockerServiceBusTopicBuilder(string name)
    {
        _name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Topic name must not be empty.", nameof(name))
            : name;
    }

    /// <summary>
    /// Adds a subscription to the topic.
    /// </summary>
    /// <param name="name">The subscription name; must not be empty.</param>
    /// <returns>This builder for chaining.</returns>
    public DockerServiceBusTopicBuilder AddSubscription(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Subscription name must not be empty.", nameof(name));

        _subscriptions.Add(new ServiceBusEmulatorSubscriptionDefinition(name));
        return this;
    }

    internal ServiceBusEmulatorTopicDefinition Build()
    {
        return new ServiceBusEmulatorTopicDefinition(_name, [.. _subscriptions]);
    }
}

internal sealed class ServiceBusTopologySource
{
    private ServiceBusTopologySource(string? configPath, ServiceBusEmulatorTopologyDefinition? topology)
    {
        ConfigPath = configPath;
        Topology = topology;
    }

    public string? ConfigPath { get; }

    public ServiceBusEmulatorTopologyDefinition? Topology { get; }

    public bool IsPath => ConfigPath is not null;

    public static ServiceBusTopologySource FromPath(string configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath))
            throw new ArgumentException("Topology config path must not be empty.", nameof(configPath));

        return new ServiceBusTopologySource(configPath, null);
    }

    public static ServiceBusTopologySource FromTopology(ServiceBusEmulatorTopologyDefinition topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        return new ServiceBusTopologySource(null, topology);
    }

    public bool SemanticallyEquals(ServiceBusTopologySource other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (IsPath && other.IsPath)
            return string.Equals(ConfigPath, other.ConfigPath, StringComparison.OrdinalIgnoreCase);

        if (!IsPath && !other.IsPath)
            return string.Equals(ServiceBusTopologySerializer.Serialize(Topology!), ServiceBusTopologySerializer.Serialize(other.Topology!), StringComparison.Ordinal);

        return false;
    }

    public string Describe()
    {
        return IsPath
            ? $"path '{ConfigPath}'"
            : "a fluent Service Bus topology";
    }
}

internal sealed record MaterializedServiceBusTopology(string ConfigPath, bool IsTemporary);

internal static class ServiceBusTopologyMaterializer
{
    internal static MaterializedServiceBusTopology Materialize(ServiceBusTopologySource source, ScopedLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.ConfigPath is not null)
            return new MaterializedServiceBusTopology(ServiceBusConfigLocator.Resolve(source.ConfigPath, logger), false);

        string topologyDirectory = ContainerLeftovers.TopologyDirectory;
        Directory.CreateDirectory(topologyDirectory);

        string topologyPath = Path.Combine(topologyDirectory, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(topologyPath, ServiceBusTopologySerializer.Serialize(source.Topology!));
        return new MaterializedServiceBusTopology(topologyPath, true);
    }
}

internal sealed class ServiceBusRuntimeState(ServiceBusContainer container, string? temporaryConfigPath) : IAsyncDisposable
{
    public ServiceBusContainer Container { get; } = container;

    public async ValueTask DisposeAsync()
    {
        // The CLI route first, like every other container teardown in the family: disposing through
        // the client can leave a container behind when the daemon is busy. On its own token, because
        // disposal has none; the CLI runner applies its own ceiling.
        await ContainerDockerCommands.ForceRemoveContainerAsync(Container, CancellationToken.None).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(temporaryConfigPath) && File.Exists(temporaryConfigPath))
            File.Delete(temporaryConfigPath);
    }
}

internal static class ServiceBusTopologySerializer
{
    internal static string Serialize(ServiceBusEmulatorTopologyDefinition topology)
    {
        ArgumentNullException.ThrowIfNull(topology);

        // Indented because the emulator's config is something a person reads when a run misbehaves, and
        // the property names are spelled out on the records rather than left to a naming policy: they are
        // the emulator's contract, not this package's preference.
        return JsonConvert.SerializeObject(topology, Formatting.Indented);
    }
}

internal sealed record ServiceBusEmulatorTopologyDefinition(
    [property: JsonProperty("UserConfig")] ServiceBusEmulatorUserConfigDefinition UserConfig);

internal sealed record ServiceBusEmulatorUserConfigDefinition(
    [property: JsonProperty("Namespaces")] IReadOnlyList<ServiceBusEmulatorNamespaceDefinition> Namespaces,
    [property: JsonProperty("Logging")] ServiceBusEmulatorLoggingDefinition Logging);

internal sealed record ServiceBusEmulatorNamespaceDefinition(
    [property: JsonProperty("Name")] string Name,
    [property: JsonProperty("Queues")] IReadOnlyList<ServiceBusEmulatorQueueDefinition> Queues,
    [property: JsonProperty("Topics")] IReadOnlyList<ServiceBusEmulatorTopicDefinition> Topics);

internal sealed record ServiceBusEmulatorQueueDefinition(
    [property: JsonProperty("Name")] string Name);

internal sealed record ServiceBusEmulatorTopicDefinition(
    [property: JsonProperty("Name")] string Name,
    [property: JsonProperty("Subscriptions")] IReadOnlyList<ServiceBusEmulatorSubscriptionDefinition> Subscriptions);

internal sealed record ServiceBusEmulatorSubscriptionDefinition(
    [property: JsonProperty("Name")] string Name);

internal sealed record ServiceBusEmulatorLoggingDefinition(
    [property: JsonProperty("Type")] string Type);
