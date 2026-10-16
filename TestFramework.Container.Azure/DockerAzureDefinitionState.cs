using System;
using System.Collections.Generic;
using System.Linq;
using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Azure.Identifier;
using TestFramework.Core.Environment;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Azure;

internal sealed class DockerAzureDefinitionState
{
    private readonly HashSet<Type> _definitionTypes = [];
    private readonly Dictionary<Type, DockerAzureDefinitionMetadata> _definitionMetadata = [];
    private readonly Dictionary<string, DockerAzureDefinitionMetadata> _definitionMetadataByIdentity = new(StringComparer.Ordinal);
    private readonly Dictionary<FunctionAppIdentifier, FunctionAppDefinitionDescriptor> _functionAppDescriptors = [];
    public List<DockerFunctionAppRegistration> FunctionApps { get; } = [];
    public ServiceBusTopologySource? ServiceBusTopologySource { get; private set; }
    public string? AzuriteImage { get; private set; }
    public string? CosmosDbImage { get; private set; }
    public string? MsSqlImage { get; private set; }
    public int? MsSqlMemoryLimitMb { get; private set; }
    public TimeSpan? MsSqlReadinessTimeout { get; private set; }
    public string? ServiceBusImage { get; private set; }
    public string? MsSqlPassword { get; private set; }
    public IReadOnlyCollection<ComponentContractBinding> LastContractBindings { get; private set; } = [];

    public void AddDefinition(DockerAzureDefinition definition)
    {
        Type definitionType = definition.GetType();
        if (!_definitionTypes.Add(definitionType))
            return;

        DockerAzureDefinitionMetadata metadata = new(
            definition,
            definitionType,
            ResolveRealizedIdentity(definition),
            [.. definition.GetDependencies()],
            [.. definition.GetProvidedContracts()],
            [.. definition.GetRequiredContracts()]);
        _definitionMetadata[definitionType] = metadata;
        _definitionMetadataByIdentity[metadata.RealizedIdentity] = metadata;

        foreach (ComponentDependency dependency in metadata.Dependencies)
            AddDependencyDefinition(dependency.ComponentType, definitionType);

        switch (definition)
        {
            case DockerAzureInfrastructureDefinition infrastructure:
                AzuriteImage = ResolveOverride(AzuriteImage, infrastructure.AzuriteImage, nameof(DockerAzureInfrastructureDefinition.AzuriteImage));
                CosmosDbImage = ResolveOverride(CosmosDbImage, infrastructure.CosmosDbImage, nameof(DockerAzureInfrastructureDefinition.CosmosDbImage));
                MsSqlImage = ResolveOverride(MsSqlImage, infrastructure.MsSqlImage, nameof(DockerAzureInfrastructureDefinition.MsSqlImage));
                MsSqlMemoryLimitMb = ResolveOverride(MsSqlMemoryLimitMb, infrastructure.MsSqlMemoryLimitMb, nameof(DockerAzureInfrastructureDefinition.MsSqlMemoryLimitMb));
                MsSqlReadinessTimeout = ResolveOverride(MsSqlReadinessTimeout, infrastructure.MsSqlReadinessTimeout, nameof(DockerAzureInfrastructureDefinition.MsSqlReadinessTimeout));
                ServiceBusImage = ResolveOverride(ServiceBusImage, infrastructure.ServiceBusImage, nameof(DockerAzureInfrastructureDefinition.ServiceBusImage));
                MsSqlPassword = ResolveOverride(MsSqlPassword, infrastructure.MsSqlPassword, nameof(DockerAzureInfrastructureDefinition.MsSqlPassword));
                if (infrastructure.GetServiceBusTopologySource() is ServiceBusTopologySource infrastructureTopologySource)
                    SetServiceBusTopologySource(infrastructureTopologySource);
                break;
            case DockerStorageDefinition:
                break;
            case DockerCosmosDefinition:
                break;
            case DockerAzureSqlDefinition:
                break;
            case DockerServiceBusDefinition serviceBus:
                if (serviceBus.GetTopologySource() is { } serviceBusTopologySource)
                    SetServiceBusTopologySource(serviceBusTopologySource);
                break;
            case DockerFunctionAppDefinition functionApp:
                FunctionAppDefinitionDescriptor descriptor = functionApp.CreateDescriptor();
                DockerAzureDefinitionMetadata functionAppMetadata = _definitionMetadata[definitionType];
                DockerAzureDefinitionMetadata mergedMetadata = functionAppMetadata with
                {
                    Dependencies = MergeDependencies(functionAppMetadata.Dependencies, descriptor.Dependencies)
                };
                _definitionMetadata[definitionType] = mergedMetadata;
                _definitionMetadataByIdentity[mergedMetadata.RealizedIdentity] = mergedMetadata;
                foreach (ComponentDependency dependency in descriptor.Dependencies)
                    AddDependencyDefinition(dependency.ComponentType, definitionType);
                AddFunctionAppDescriptor(functionApp.Identifier, descriptor);
                foreach (ServiceBusTopologySource source in descriptor.ServiceBusTopologySources)
                    SetServiceBusTopologySource(source);
                break;
            default:
                throw new UnsupportedFrameworkValueException($"Unsupported Docker Azure definition type '{definition.GetType().FullName}'.");
        }
    }

    private void AddDependencyDefinition(Type dependencyType, Type ownerType)
    {
        if (!typeof(DockerAzureDefinition).IsAssignableFrom(dependencyType))
            throw new FrameworkConfigurationException($"Dependency type '{dependencyType.FullName}' declared by '{ownerType.FullName}' is not a Docker Azure definition.");

        if (dependencyType.IsAbstract)
            throw new FrameworkConfigurationException($"Dependency type '{dependencyType.FullName}' declared by '{ownerType.FullName}' must be a concrete Docker Azure definition.");

        if (Activator.CreateInstance(dependencyType) is not DockerAzureDefinition dependencyDefinition)
            throw new FrameworkStateException($"Dependency type '{dependencyType.FullName}' declared by '{ownerType.FullName}' could not be constructed.");

        AddDefinition(dependencyDefinition);
    }

    public IReadOnlyCollection<ComponentContractBinding> ValidateAndBindContracts(Func<IEnvironmentResourceContract, IEnvironmentResourceContract, bool> matcher)
    {
        ComponentGraphNode[] nodes = [.. _definitionMetadata.Values.Select(CreateGraphNode)];
        ComponentGraphValidator.Validate(nodes);
        LastContractBindings = ContractBindingPass.Bind(nodes, matcher);
        return LastContractBindings;
    }

    public IReadOnlyCollection<DockerAzureDefinitionMetadata> ExpandActivatedDefinitions(
        IEnumerable<FunctionAppIdentifier> functionAppIdentifiers,
        IEnumerable<StorageAccountIdentifier> storageIdentifiers,
        IEnumerable<CosmosContainerIdentifier> cosmosIdentifiers,
        IEnumerable<SqlDatabaseIdentifier> sqlIdentifiers,
        IEnumerable<ServiceBusIdentifier> serviceBusIdentifiers,
        IEnumerable<ComponentContractBinding> bindings)
    {
        Dictionary<string, List<ComponentContractBinding>> bindingsByConsumer = bindings
            .GroupBy(x => x.ConsumerIdentity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        Queue<string> pending = new();
        foreach (FunctionAppIdentifier identifier in functionAppIdentifiers)
            EnqueueKnownIdentity(pending, $"functionapp:{identifier}");
        foreach (StorageAccountIdentifier identifier in storageIdentifiers)
            EnqueueKnownIdentity(pending, $"storage:{identifier}");
        foreach (CosmosContainerIdentifier identifier in cosmosIdentifiers)
            EnqueueKnownIdentity(pending, $"cosmos:{identifier}");
        foreach (SqlDatabaseIdentifier identifier in sqlIdentifiers)
            EnqueueKnownIdentity(pending, $"sql:{identifier}");
        foreach (ServiceBusIdentifier identifier in serviceBusIdentifiers)
            EnqueueKnownIdentity(pending, $"servicebus:{identifier}");

        HashSet<string> visited = new(StringComparer.Ordinal);
        List<DockerAzureDefinitionMetadata> activated = [];
        while (pending.Count > 0)
        {
            string identity = pending.Dequeue();
            if (!visited.Add(identity) || !_definitionMetadataByIdentity.TryGetValue(identity, out DockerAzureDefinitionMetadata? metadata))
                continue;

            activated.Add(metadata);

            foreach (ComponentGraphDependency dependency in CreateGraphNode(metadata).Dependencies)
                EnqueueKnownIdentity(pending, dependency.RealizedComponentIdentity);

            if (bindingsByConsumer.TryGetValue(identity, out List<ComponentContractBinding>? consumerBindings))
            {
                foreach (ComponentContractBinding binding in consumerBindings)
                    EnqueueKnownIdentity(pending, binding.ProviderIdentity);
            }
        }

        return activated;
    }

    public FunctionAppDefinitionDescriptor GetRequiredFunctionAppDescriptor(FunctionAppIdentifier identifier)
    {
        if (_functionAppDescriptors.TryGetValue(identifier, out FunctionAppDefinitionDescriptor? descriptor))
            return descriptor;

        throw new FrameworkConfigurationException($"No Docker Function App registration was configured for identifier '{identifier}'.");
    }

    public bool TryGetFunctionAppDescriptor(FunctionAppIdentifier identifier, out FunctionAppDefinitionDescriptor? descriptor)
    {
        return _functionAppDescriptors.TryGetValue(identifier, out descriptor);
    }

    /// <summary>
    /// Every configuration a definition supplies for itself, whether or not this run turns out to use it.
    /// </summary>
    /// <remarks>
    /// Enumerated from the definitions rather than from resolved usage, because a run composes its
    /// resources before a single step has run and usage is decided later - inside the step that creates
    /// the components. Declaring one nobody uses costs a node no reader ever asks about; waiting for
    /// usage would mean declaring after the graph was built, which is the thing declarations are not
    /// allowed to do.
    /// </remarks>
    /// <returns>The record type, the identifier it belongs to, and the record.</returns>
    public IEnumerable<(Type ConfigType, string Identifier, object Config)> DefaultConfigs()
    {
        foreach (DockerAzureDefinitionMetadata metadata in _definitionMetadata.Values)
        {
            switch (metadata.Definition)
            {
                case DockerStorageDefinition storage when storage.TryCreateDefaultConfig(out StorageAccountConfig storageConfig):
                    yield return (typeof(StorageAccountConfig), storage.Identifier, storageConfig);
                    break;
                case DockerCosmosDefinition cosmos when cosmos.TryCreateDefaultConfig(out CosmosContainerDbConfig cosmosConfig):
                    yield return (typeof(CosmosContainerDbConfig), cosmos.Identifier, cosmosConfig);
                    break;
                case DockerAzureSqlDefinition sql when sql.TryCreateDefaultConfig(out SqlDatabaseConfig sqlConfig):
                    yield return (typeof(SqlDatabaseConfig), sql.Identifier, sqlConfig);
                    break;
                case DockerServiceBusDefinition serviceBus when serviceBus.TryCreateDefaultConfig(out ServiceBusConfig serviceBusConfig):
                    yield return (typeof(ServiceBusConfig), serviceBus.Identifier, serviceBusConfig);
                    break;
                case DockerFunctionAppDefinition functionApp when functionApp.TryCreateDefaultConfig(out FunctionAppConfig functionAppConfig):
                    yield return (typeof(FunctionAppConfig), functionApp.Identifier, functionAppConfig);
                    break;
            }
        }
    }

    public bool TryGetDefaultConfig(Type configType, string identifier, out object? config)
    {
        if (!_definitionMetadataByIdentity.TryGetValue(GetRealizedIdentity(configType, identifier), out DockerAzureDefinitionMetadata? metadata))
        {
            config = null;
            return false;
        }

        switch (metadata.Definition)
        {
            case DockerStorageDefinition storage when configType == typeof(StorageAccountConfig) && storage.TryCreateDefaultConfig(out StorageAccountConfig storageConfig):
                config = storageConfig;
                return true;
            case DockerCosmosDefinition cosmos when configType == typeof(CosmosContainerDbConfig) && cosmos.TryCreateDefaultConfig(out CosmosContainerDbConfig cosmosConfig):
                config = cosmosConfig;
                return true;
            case DockerAzureSqlDefinition sql when configType == typeof(SqlDatabaseConfig) && sql.TryCreateDefaultConfig(out SqlDatabaseConfig sqlConfig):
                config = sqlConfig;
                return true;
            case DockerServiceBusDefinition serviceBus when configType == typeof(ServiceBusConfig) && serviceBus.TryCreateDefaultConfig(out ServiceBusConfig serviceBusConfig):
                config = serviceBusConfig;
                return true;
            case DockerFunctionAppDefinition functionApp when configType == typeof(FunctionAppConfig) && functionApp.TryCreateDefaultConfig(out FunctionAppConfig functionAppConfig):
                config = functionAppConfig;
                return true;
            default:
                config = null;
                return false;
        }
    }

    private void AddFunctionAppDescriptor(FunctionAppIdentifier identifier, FunctionAppDefinitionDescriptor descriptor)
    {
        if (_functionAppDescriptors.TryGetValue(identifier, out FunctionAppDefinitionDescriptor? existing))
        {
            if (existing.DefinitionType != descriptor.DefinitionType)
                throw new FrameworkConfigurationException($"Docker Function App identifier '{identifier}' was configured by multiple definitions.");

            return;
        }

        _functionAppDescriptors[identifier] = descriptor;
        FunctionApps.Add(descriptor.Registration);
    }

    private void SetServiceBusTopologySource(ServiceBusTopologySource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (ServiceBusTopologySource is null)
        {
            ServiceBusTopologySource = source;
            return;
        }

        if (!ServiceBusTopologySource.SemanticallyEquals(source))
            throw new FrameworkConfigurationException($"Multiple Service Bus topology sources were configured: {ServiceBusTopologySource.Describe()} and {source.Describe()}.");
    }

    private static string? ResolveOverride(string? current, string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return current;

        if (current is null)
            return value;

        if (!string.Equals(current, value, StringComparison.Ordinal))
            throw new FrameworkConfigurationException($"Multiple values were configured for {propertyName}: '{current}' and '{value}'.");

        return current;
    }

    private static T? ResolveOverride<T>(T? current, T? value, string propertyName)
        where T : struct
    {
        if (value is null)
            return current;

        if (current is null)
            return value;

        if (!EqualityComparer<T>.Default.Equals(current.Value, value.Value))
            throw new FrameworkConfigurationException($"Multiple values were configured for {propertyName}: '{current}' and '{value}'.");

        return current;
    }

    private ComponentGraphNode CreateGraphNode(DockerAzureDefinitionMetadata metadata)
    {
        ComponentGraphDependency[] dependencies = [.. metadata.Dependencies.Select(dependency =>
            new ComponentGraphDependency
            {
                ComponentType = dependency.ComponentType,
                RealizedComponentIdentity = _definitionMetadata[dependency.ComponentType].RealizedIdentity,
                Ownership = dependency.Ownership,
            })];

        return new ComponentGraphNode
        {
            ComponentType = metadata.DefinitionType,
            RealizedComponentIdentity = metadata.RealizedIdentity,
            Dependencies = dependencies,
            Provides = metadata.Provides,
            Requires = metadata.Requires,
        };
    }

    private void EnqueueKnownIdentity(Queue<string> pending, string identity)
    {
        if (_definitionMetadataByIdentity.ContainsKey(identity))
            pending.Enqueue(identity);
    }

    private static string GetRealizedIdentity(Type configType, string identifier)
    {
        if (configType == typeof(StorageAccountConfig))
            return $"storage:{identifier}";
        if (configType == typeof(CosmosContainerDbConfig))
            return $"cosmos:{identifier}";
        if (configType == typeof(SqlDatabaseConfig))
            return $"sql:{identifier}";
        if (configType == typeof(ServiceBusConfig))
            return $"servicebus:{identifier}";
        if (configType == typeof(FunctionAppConfig))
            return $"functionapp:{identifier}";
        throw new UnsupportedFrameworkValueException($"Unsupported config type '{configType.FullName}' for Docker Azure default config lookup.");
    }

    private static IReadOnlyCollection<ComponentDependency> MergeDependencies(
        IEnumerable<ComponentDependency> left,
        IEnumerable<ComponentDependency> right)
    {
        Dictionary<Type, ComponentDependency> merged = new();
        foreach (ComponentDependency dependency in left.Concat(right))
        {
            if (merged.TryGetValue(dependency.ComponentType, out ComponentDependency? existing) && existing is not null && existing.Ownership != dependency.Ownership)
            {
                throw new FrameworkConfigurationException($"Dependency '{dependency.ComponentType.FullName}' was configured with conflicting ownership values.");
            }

            merged[dependency.ComponentType] = dependency;
        }

        return merged.Values.ToArray();
    }

    private static string ResolveRealizedIdentity(DockerAzureDefinition definition)
    {
        return definition switch
        {
            DockerStorageDefinition storage => $"storage:{storage.Identifier}",
            DockerCosmosDefinition cosmos => $"cosmos:{cosmos.Identifier}",
            DockerAzureSqlDefinition sql => $"sql:{sql.Identifier}",
            DockerServiceBusDefinition serviceBus => $"servicebus:{serviceBus.Identifier}",
            DockerFunctionAppDefinition functionApp => $"functionapp:{functionApp.Identifier}",
            DockerAzureInfrastructureDefinition infrastructure => $"infrastructure:{infrastructure.GetType().FullName}",
            _ => $"definition:{definition.GetType().FullName}"
        };
    }
}
