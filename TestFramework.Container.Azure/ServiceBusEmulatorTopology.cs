using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Testcontainers.ServiceBus;

namespace TestFramework.Container.Azure;

public sealed class DockerServiceBusTopologyBuilder
{
    private readonly List<ServiceBusEmulatorNamespaceDefinition> _namespaces = [];

    public DockerServiceBusTopologyBuilder AddNamespace(string name, Action<DockerServiceBusNamespaceBuilder>? configure = null)
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
            throw new InvalidOperationException("A Service Bus emulator topology must declare at least one namespace.");

        return new ServiceBusEmulatorTopologyDefinition(new ServiceBusEmulatorUserConfigDefinition([.. _namespaces], new ServiceBusEmulatorLoggingDefinition("Console")));
    }
}

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

    public DockerServiceBusNamespaceBuilder AddQueue(string name)
    {
        _queues.Add(new ServiceBusEmulatorQueueDefinition(RequireName(name, "Queue")));
        return this;
    }

    public DockerServiceBusNamespaceBuilder AddTopic(string name, Action<DockerServiceBusTopicBuilder>? configure = null)
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
    internal static MaterializedServiceBusTopology Materialize(ServiceBusTopologySource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.ConfigPath is not null)
            return new MaterializedServiceBusTopology(ServiceBusConfigLocator.Resolve(source.ConfigPath), false);

        string topologyDirectory = Path.Combine(Path.GetTempPath(), "TestFramework", "servicebus-topologies");
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
        await Container.DisposeAsync().ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(temporaryConfigPath) && File.Exists(temporaryConfigPath))
            File.Delete(temporaryConfigPath);
    }
}

internal static class ServiceBusTopologySerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    internal static string Serialize(ServiceBusEmulatorTopologyDefinition topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        return JsonSerializer.Serialize(topology, SerializerOptions);
    }
}

internal sealed record ServiceBusEmulatorTopologyDefinition(
    [property: JsonPropertyName("UserConfig")] ServiceBusEmulatorUserConfigDefinition UserConfig);

internal sealed record ServiceBusEmulatorUserConfigDefinition(
    [property: JsonPropertyName("Namespaces")] IReadOnlyList<ServiceBusEmulatorNamespaceDefinition> Namespaces,
    [property: JsonPropertyName("Logging")] ServiceBusEmulatorLoggingDefinition Logging);

internal sealed record ServiceBusEmulatorNamespaceDefinition(
    [property: JsonPropertyName("Name")] string Name,
    [property: JsonPropertyName("Queues")] IReadOnlyList<ServiceBusEmulatorQueueDefinition> Queues,
    [property: JsonPropertyName("Topics")] IReadOnlyList<ServiceBusEmulatorTopicDefinition> Topics);

internal sealed record ServiceBusEmulatorQueueDefinition(
    [property: JsonPropertyName("Name")] string Name);

internal sealed record ServiceBusEmulatorTopicDefinition(
    [property: JsonPropertyName("Name")] string Name,
    [property: JsonPropertyName("Subscriptions")] IReadOnlyList<ServiceBusEmulatorSubscriptionDefinition> Subscriptions);

internal sealed record ServiceBusEmulatorSubscriptionDefinition(
    [property: JsonPropertyName("Name")] string Name);

internal sealed record ServiceBusEmulatorLoggingDefinition(
    [property: JsonPropertyName("Type")] string Type);
