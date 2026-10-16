using System;
using System.Collections.Generic;
using System.Linq;
using TestFramework.Core.Environment;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Azure;

/// <summary>
/// Collects a Function App's resource bindings and extra app settings; passed to
/// <see cref="DockerFunctionAppDefinition"/>'s <c>Configure</c> override.
/// </summary>
/// <remarks>
/// Each binding kind (storage, Cosmos, Service Bus trigger, Service Bus reply) is held once: binding the same kind
/// again replaces the earlier binding.
/// </remarks>
public sealed class DockerFunctionAppBuilder
{
    private readonly Dictionary<Type, ComponentDependency> _dependencies = [];

    internal Dictionary<string, string> AdditionalSettings { get; } = [];
    internal List<ServiceBusTopologySource> ServiceBusTopologySources { get; } = [];
    internal List<FunctionAppResourceBinding> ResourceBindings { get; } = [];
    internal IReadOnlyCollection<ComponentDependency> Dependencies => _dependencies.Values;

    /// <summary>
    /// Binds a storage definition into the Function App container settings, under the template's setting names.
    /// </summary>
    /// <typeparam name="TStorage">The storage definition.</typeparam>
    /// <returns>This builder.</returns>
    public DockerFunctionAppBuilder UseStorage<TStorage>()
        where TStorage : DockerStorageDefinition, new()
        => UseStorage<TStorage>(FunctionAppStorageBinding.Default);

    /// <summary>
    /// Binds a storage definition into the Function App container settings.
    /// </summary>
    /// <typeparam name="TStorage">The storage definition.</typeparam>
    /// <param name="binding">Which app settings carry what, and whether the storage is shared.</param>
    /// <returns>This builder.</returns>
    public DockerFunctionAppBuilder UseStorage<TStorage>(FunctionAppStorageBinding binding)
        where TStorage : DockerStorageDefinition, new()
    {
        ArgumentNullException.ThrowIfNull(binding);

        TStorage definition = new();
        AddDependency(typeof(TStorage), binding.Ownership);
        ReplaceResourceBinding(new FunctionAppResourceBinding(
            FunctionAppResourceBindingKind.Storage,
            definition.Identifier,
            binding.ConnectionSettingName,
            binding.HostStorageSettingName,
            binding.TableNameSettingName));
        return this;
    }

    /// <summary>
    /// Binds a Cosmos definition into the Function App container settings, under the template's setting names.
    /// </summary>
    /// <typeparam name="TCosmos">The Cosmos definition.</typeparam>
    /// <returns>This builder.</returns>
    public DockerFunctionAppBuilder UseCosmos<TCosmos>()
        where TCosmos : DockerCosmosDefinition, new()
        => UseCosmos<TCosmos>(FunctionAppCosmosBinding.Default);

    /// <summary>
    /// Binds a Cosmos definition into the Function App container settings.
    /// </summary>
    /// <typeparam name="TCosmos">The Cosmos definition.</typeparam>
    /// <param name="binding">Which app settings carry what, and whether the emulator is shared.</param>
    /// <returns>This builder.</returns>
    public DockerFunctionAppBuilder UseCosmos<TCosmos>(FunctionAppCosmosBinding binding)
        where TCosmos : DockerCosmosDefinition, new()
    {
        ArgumentNullException.ThrowIfNull(binding);

        TCosmos definition = new();
        AddDependency(typeof(TCosmos), binding.Ownership);
        ReplaceResourceBinding(new FunctionAppResourceBinding(
            FunctionAppResourceBindingKind.Cosmos,
            definition.Identifier,
            binding.ConnectionSettingName,
            binding.DatabaseSettingName,
            binding.ContainerSettingName));
        return this;
    }

    /// <summary>
    /// Binds a Service Bus endpoint as the Function App's trigger, under the template's setting names.
    /// </summary>
    /// <typeparam name="TServiceBus">The Service Bus definition.</typeparam>
    /// <param name="endpointSelector">Picks the endpoint from the definition.</param>
    /// <returns>This builder.</returns>
    public DockerFunctionAppBuilder UseServiceBusTrigger<TServiceBus>(Func<TServiceBus, DockerServiceBusEndpoint> endpointSelector)
        where TServiceBus : DockerServiceBusDefinition, new()
        => UseServiceBusTrigger(endpointSelector, FunctionAppServiceBusTriggerBinding.Default);

    /// <summary>
    /// Binds a Service Bus endpoint as the Function App's trigger.
    /// </summary>
    /// <typeparam name="TServiceBus">The Service Bus definition.</typeparam>
    /// <param name="endpointSelector">Picks the endpoint from the definition.</param>
    /// <param name="binding">Which app settings carry what, and whether the emulator is shared.</param>
    /// <returns>This builder.</returns>
    public DockerFunctionAppBuilder UseServiceBusTrigger<TServiceBus>(Func<TServiceBus, DockerServiceBusEndpoint> endpointSelector, FunctionAppServiceBusTriggerBinding binding)
        where TServiceBus : DockerServiceBusDefinition, new()
    {
        ArgumentNullException.ThrowIfNull(endpointSelector);
        ArgumentNullException.ThrowIfNull(binding);

        TServiceBus definition = new();
        DockerServiceBusEndpoint endpoint = endpointSelector(definition);
        AddDependency(typeof(TServiceBus), binding.Ownership);
        ReplaceResourceBinding(new FunctionAppResourceBinding(
            FunctionAppResourceBindingKind.ServiceBusTrigger,
            definition.Identifier,
            binding.ConnectionSettingName,
            binding.EntitySettingName,
            binding.SubscriptionSettingName,
            endpoint));
        AddServiceBusTopologySource(definition.GetTopologySource());
        return this;
    }

    /// <summary>
    /// Binds a Service Bus endpoint as the entity the Function App replies to, under the template's setting names.
    /// </summary>
    /// <typeparam name="TServiceBus">The Service Bus definition.</typeparam>
    /// <param name="endpointSelector">Picks the endpoint from the definition.</param>
    /// <returns>This builder.</returns>
    public DockerFunctionAppBuilder UseServiceBusReply<TServiceBus>(Func<TServiceBus, DockerServiceBusEndpoint> endpointSelector)
        where TServiceBus : DockerServiceBusDefinition, new()
        => UseServiceBusReply(endpointSelector, FunctionAppServiceBusReplyBinding.Default);

    /// <summary>
    /// Binds a Service Bus endpoint as the entity the Function App replies to.
    /// </summary>
    /// <typeparam name="TServiceBus">The Service Bus definition.</typeparam>
    /// <param name="endpointSelector">Picks the endpoint from the definition.</param>
    /// <param name="binding">Which app settings carry what, and whether the emulator is shared.</param>
    /// <returns>This builder.</returns>
    public DockerFunctionAppBuilder UseServiceBusReply<TServiceBus>(Func<TServiceBus, DockerServiceBusEndpoint> endpointSelector, FunctionAppServiceBusReplyBinding binding)
        where TServiceBus : DockerServiceBusDefinition, new()
    {
        ArgumentNullException.ThrowIfNull(endpointSelector);
        ArgumentNullException.ThrowIfNull(binding);

        TServiceBus definition = new();
        DockerServiceBusEndpoint endpoint = endpointSelector(definition);
        AddDependency(typeof(TServiceBus), binding.Ownership);
        ReplaceResourceBinding(new FunctionAppResourceBinding(
            FunctionAppResourceBindingKind.ServiceBusReply,
            definition.Identifier,
            binding.ConnectionSettingName,
            binding.EntitySettingName,
            ServiceBusEndpoint: endpoint));
        AddServiceBusTopologySource(definition.GetTopologySource());
        return this;
    }

    /// <summary>
    /// Adds an app setting the Functions host container starts with. Setting the same key again replaces the value.
    /// </summary>
    /// <param name="key">The setting name.</param>
    /// <param name="value">The setting value.</param>
    /// <returns>This builder.</returns>
    public DockerFunctionAppBuilder WithAppSetting(string key, string value)
    {
        AdditionalSettings[key] = value;
        return this;
    }

    private void AddDependency(Type dependencyType, DependencyOwnership ownership)
    {
        ComponentDependency dependency = new() { ComponentType = dependencyType, Ownership = ownership };

        if (_dependencies.TryGetValue(dependencyType, out ComponentDependency? existing) && existing is not null && existing.Ownership != ownership)
            throw new FrameworkConfigurationException($"Dependency '{dependencyType.FullName}' was configured with conflicting ownership values.");

        _dependencies[dependencyType] = dependency;
    }

    private void ReplaceResourceBinding(FunctionAppResourceBinding binding)
    {
        ResourceBindings.RemoveAll(existing => existing.Kind == binding.Kind);
        ResourceBindings.Add(binding);
    }

    private void AddServiceBusTopologySource(ServiceBusTopologySource? source)
    {
        if (source is null)
            return;

        if (ServiceBusTopologySources.Any(existing => existing.SemanticallyEquals(source)))
            return;

        ServiceBusTopologySources.Add(source);
    }
}
