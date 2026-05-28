using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using TestFramework.Azure;
using TestFramework.Azure.Configuration;
using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Azure.DB.CosmosDB;
using TestFramework.Azure.DB.SqlServer;
using TestFramework.Azure.Identifier;
using TestFramework.Azure.LogicApp;
using TestFramework.Azure.StorageAccount.Blob;
using TestFramework.Azure.StorageAccount.Table;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;
using TestFramework.Core.Logging;

namespace TestFramework.Container.Azure;

public class DockerAzureEnvironment : EnvironmentProviderBase, IRunScopedServiceProviderFactory, IPersistentEnvironmentStateSink
{
    public static readonly EnvComponentIdentifier NetworkComponentId = "docker-network";
    public static readonly EnvComponentIdentifier FunctionAppComponentId = "functionapp";
    public static readonly EnvComponentIdentifier MsSqlComponentId = "mssql";
    public static readonly EnvComponentIdentifier AzuriteComponentId = "azurite";
    public static readonly EnvComponentIdentifier CosmosDbComponentId = "cosmos-emulator";
    public static readonly EnvComponentIdentifier ServiceBusComponentId = "servicebus-emulator";
    public const string AzuriteNetworkAlias = "azurite";
    public const string CosmosDbNetworkAlias = "cosmos-emulator";
    public const string ServiceBusNetworkAlias = "servicebus-emulator";
    private static readonly DockerEndpointMap EndpointMapInstance = new();

    private readonly Dictionary<EnvComponentIdentifier, object?> _runtimeStates = [];
    private readonly Dictionary<Type, object> _synthesizedConfigStores = [];
    private readonly object _runtimeStateGate = new();
    private readonly object _configStoreGate = new();
    private readonly DockerAzureDefinitionState _definitionState = new();
    private readonly Dictionary<Type, DockerAzureDefinition> _includedDefinitions = [];
    private DockerAzureResolutionSnapshot _lastResolutionSnapshot = DockerAzureResolutionSnapshot.Empty;
    private bool _resolutionSummaryLogged;
    public HashSet<string> UsedStorageIdentifiers { get; } = [];
    public HashSet<string> UsedCosmosIdentifiers { get; } = [];
    public HashSet<string> UsedSqlIdentifiers { get; } = [];
    public HashSet<string> UsedServiceBusIdentifiers { get; } = [];
    public HashSet<string> UsedFunctionAppIdentifiers { get; } = [];
    internal Dictionary<string, string> CosmosPartitionKeyPaths { get; } = [];

    public DockerAzureEnvironment()
    {
        AddComponent(new Components.DockerNetworkEnvComponent());
        AddComponent(new Components.FunctionAppEnvComponent());
        AddComponent(new Components.MsSqlEnvComponent());
        AddComponent(new Components.AzuriteEnvComponent());
        AddComponent(new Components.CosmosDbEnvComponent());
        AddComponent(new Components.ServiceBusEnvComponent());

        MapResourceKind(AzureEnvironmentResourceKinds.FunctionApp, FunctionAppComponentId);
        MapResourceKind(AzureEnvironmentResourceKinds.Storage, AzuriteComponentId);
        MapResourceKind(AzureEnvironmentResourceKinds.Cosmos, CosmosDbComponentId);
        MapResourceKind(AzureEnvironmentResourceKinds.Sql, MsSqlComponentId);
        MapResourceKind(AzureEnvironmentResourceKinds.ServiceBus, ServiceBusComponentId);

        MapArtifact<StorageAccountBlobArtifactDescriber>(AzuriteComponentId);
        MapArtifact(typeof(TableStorageEntityArtifactDescriber<>), AzuriteComponentId);
        MapArtifact(typeof(CosmosDbItemArtifactDescriber<>), CosmosDbComponentId);
        MapArtifact(typeof(SqlRowArtifactDescriber<>), MsSqlComponentId);
    }

    public DockerAzureEnvironment Include<TDefinition>()
        where TDefinition : DockerAzureDefinition, new()
    {
        return Include(new TDefinition());
    }

    public override bool SupportsParallelComponentCreation => true;

    public DockerAzureEnvironment Include(DockerAzureDefinition definition)
    {
        _includedDefinitions.TryAdd(definition.GetType(), definition);
        _definitionState.AddDefinition(definition);
        return this;
    }

    public static DockerAzureEnvironment For<TDefinition>()
        where TDefinition : DockerAzureDefinition, new()
    {
        return new DockerAzureEnvironment().Include<TDefinition>();
    }

    public DockerAzureEnvironment UseFunctionApp<TFunctionApp>(FunctionAppIdentifier identifier, Action<DockerFunctionAppBuilder>? configure = null, string? image = null)
    {
        return Include(new InlineFunctionAppDefinition<TFunctionApp>(identifier, configure, image));
    }

    public static DockerAzureEnvironment ForFunctionApp<TFunctionApp>(FunctionAppIdentifier identifier, Action<DockerFunctionAppBuilder>? configure = null, string? image = null)
    {
        return new DockerAzureEnvironment().UseFunctionApp<TFunctionApp>(identifier, configure, image);
    }

    public static DockerAzureEnvironment ForFunctionAppWithStorage<TFunctionApp, TStorage>(FunctionAppIdentifier identifier, string? image = null)
        where TStorage : DockerStorageDefinition, new()
    {
        return ForFunctionApp<TFunctionApp>(identifier, builder => builder.UseStorage<TStorage>(), image);
    }

    public static DockerAzureEnvironment ForFunctionAppWithStorageAndCosmos<TFunctionApp, TStorage, TCosmos>(FunctionAppIdentifier identifier, string? image = null)
        where TStorage : DockerStorageDefinition, new()
        where TCosmos : DockerCosmosDefinition, new()
    {
        return ForFunctionApp<TFunctionApp>(identifier, builder => builder
            .UseStorage<TStorage>()
            .UseCosmos<TCosmos>(), image);
    }

    public static DockerAzureEnvironment ForFunctionAppWithStorageAndServiceBus<TFunctionApp, TStorage, TServiceBus>(FunctionAppIdentifier identifier, string? image = null)
        where TStorage : DockerStorageDefinition, new()
        where TServiceBus : DockerServiceBusDefinition, new()
    {
        return ForFunctionApp<TFunctionApp>(identifier, builder => builder
            .UseStorage<TStorage>()
            .UseServiceBusTrigger<TServiceBus>()
            .UseServiceBusReply<TServiceBus>(), image);
    }

    public static DockerAzureEnvironment ForFunctionAppWithCommonBindings<TFunctionApp, TStorage, TCosmos, TServiceBus>(FunctionAppIdentifier identifier, string? image = null)
        where TStorage : DockerStorageDefinition, new()
        where TCosmos : DockerCosmosDefinition, new()
        where TServiceBus : DockerServiceBusDefinition, new()
    {
        return ForFunctionApp<TFunctionApp>(identifier, builder => builder
            .UseStorage<TStorage>()
            .UseCosmos<TCosmos>()
            .UseServiceBusTrigger<TServiceBus>()
            .UseServiceBusReply<TServiceBus>(), image);
    }

    public override IReadOnlyCollection<EnvComponentIdentifier> ResolveComponents(IEnumerable<ArtifactInstanceGeneric> artifacts, IEnumerable<EnvironmentRequirement> requirements)
    {
        UsedStorageIdentifiers.Clear();
        UsedCosmosIdentifiers.Clear();
        UsedSqlIdentifiers.Clear();
        UsedServiceBusIdentifiers.Clear();
        UsedFunctionAppIdentifiers.Clear();
        CosmosPartitionKeyPaths.Clear();
        _synthesizedConfigStores.Clear();
        _resolutionSummaryLogged = false;

        foreach (ArtifactInstanceGeneric artifact in artifacts)
            CaptureIdentifiers(artifact.Reference);

        IReadOnlyCollection<ComponentContractBinding> contractBindings = _definitionState.ValidateAndBindContracts(DockerAzureContractMatcher.IsMatch);
        HashSet<EnvComponentIdentifier> resolved = [.. base.ResolveComponents(artifacts, requirements)];
        CaptureActivatedDefinitionUsage(_definitionState.ExpandActivatedDefinitions(
            UsedFunctionAppIdentifiers.Select(x => new FunctionAppIdentifier(x)),
            UsedStorageIdentifiers.Select(x => new StorageAccountIdentifier(x)),
            UsedCosmosIdentifiers.Select(x => new CosmosContainerIdentifier(x)),
            UsedSqlIdentifiers.Select(x => new SqlDatabaseIdentifier(x)),
            UsedServiceBusIdentifiers.Select(x => new ServiceBusIdentifier(x)),
            contractBindings));

        if (UsedFunctionAppIdentifiers.Count > 0)
            resolved.Add(FunctionAppComponentId);
        if (UsedStorageIdentifiers.Count > 0)
            resolved.Add(AzuriteComponentId);
        if (UsedCosmosIdentifiers.Count > 0)
            resolved.Add(CosmosDbComponentId);
        if (UsedSqlIdentifiers.Count > 0)
            resolved.Add(MsSqlComponentId);
        if (UsedServiceBusIdentifiers.Count > 0)
            resolved.Add(ServiceBusComponentId);

        ValidateFunctionAppRegistrations();
        _lastResolutionSnapshot = CreateResolutionSnapshot(resolved, contractBindings);

        return [.. resolved];
    }

    internal void LogPendingResolutionSummary(ScopedLogger logger)
    {
        if (_resolutionSummaryLogged || _lastResolutionSnapshot == DockerAzureResolutionSnapshot.Empty)
            return;

        _resolutionSummaryLogged = true;
        foreach (string line in _lastResolutionSnapshot.ToLogLines())
            logger.LogInformation(line);
    }

    internal void SetRuntimeState(EnvComponentIdentifier identifier, object? state)
    {
        lock (_runtimeStateGate)
            _runtimeStates[identifier] = state;
    }

    public void SetPersistentState(EnvComponentIdentifier identifier, object? state)
    {
        SetRuntimeState(identifier, state);
    }

    internal DockerEndpointMap GetEndpointMap()
    {
        return EndpointMapInstance;
    }

    internal T GetRequiredRuntimeState<T>(EnvComponentIdentifier identifier)
    {
        lock (_runtimeStateGate)
        {
            if (_runtimeStates.TryGetValue(identifier, out object? state) && state is T typedState)
                return typedState;
        }

        throw new InvalidOperationException($"The runtime state for environment component '{identifier}' is not available.");
    }

    internal IReadOnlyCollection<DockerFunctionAppRegistration> GetFunctionAppRegistrations()
    {
        return [.. _definitionState.FunctionApps];
    }

    internal FunctionAppDefinitionDescriptor GetRequiredFunctionAppDescriptor(FunctionAppIdentifier identifier)
    {
        return _definitionState.GetRequiredFunctionAppDescriptor(identifier);
    }

    internal IReadOnlyCollection<ComponentContractBinding> GetContractBindings()
    {
        return _definitionState.LastContractBindings;
    }

    internal ServiceBusTopologySource GetServiceBusTopologySource()
    {
        return _definitionState.ServiceBusTopologySource ?? ServiceBusTopologySource.FromPath(DockerAzureDefaults.ServiceBusTopologyConfigPath);
    }

    internal string GetServiceBusTopologyConfigPath()
    {
        ServiceBusTopologySource source = GetServiceBusTopologySource();
        return source.ConfigPath ?? throw new InvalidOperationException("The active Service Bus topology was configured fluently and does not have a backing file path.");
    }

    internal string GetAzuriteImage()
    {
        return _definitionState.AzuriteImage ?? DockerAzureDefaults.AzuriteImage;
    }

    internal string GetCosmosDbImage()
    {
        return _definitionState.CosmosDbImage ?? DockerAzureDefaults.CosmosDbImage;
    }

    internal string GetMsSqlImage()
    {
        return _definitionState.MsSqlImage ?? DockerAzureDefaults.MsSqlImage;
    }

    internal int GetMsSqlMemoryLimitMb()
    {
        return _definitionState.MsSqlMemoryLimitMb ?? DockerAzureDefaults.MsSqlMemoryLimitMb;
    }

    internal string GetServiceBusImage()
    {
        return _definitionState.ServiceBusImage ?? DockerAzureDefaults.ServiceBusImage;
    }

    internal string GetMsSqlPassword()
    {
        return _definitionState.MsSqlPassword ?? DockerAzureDefaults.MsSqlPassword;
    }

    internal ConfigStore<TConfig>? GetOrCreateConfigStore<TConfig>(IServiceProvider serviceProvider, IReadOnlyCollection<string> identifiers, string componentName)
    {
        if (identifiers.Count == 0)
            return null;

        ConfigStore<TConfig>? store = serviceProvider.GetService(typeof(ConfigStore<TConfig>)) as ConfigStore<TConfig>;
        lock (_configStoreGate)
        {
            if (store is null)
            {
                if (!_synthesizedConfigStores.TryGetValue(typeof(TConfig), out object? synthesizedStore))
                {
                    synthesizedStore = new ConfigStore<TConfig>();
                    _synthesizedConfigStores[typeof(TConfig)] = synthesizedStore;
                }

                store = (ConfigStore<TConfig>)synthesizedStore;
            }

            foreach (string identifier in identifiers.OrderBy(x => x, StringComparer.Ordinal))
                EnsureConfigPresent(store, identifier, componentName);
        }

        return store;
    }

    public IServiceProvider CreateRunScopedServiceProvider(IServiceProvider baseServiceProvider)
    {
        return new DockerAzureRunScopedServiceProvider(this, baseServiceProvider);
    }

    private void CaptureIdentifiers(ArtifactReferenceGeneric reference)
    {
        Type referenceType = reference.GetType();
        if (reference is StorageAccountBlobArtifactReference blobReference)
        {
            UsedStorageIdentifiers.Add(blobReference.Identifier);
            return;
        }

        if (TryReadIdentifier(reference, referenceType, "Identifier", out string? storageIdentifier))
            UsedStorageIdentifiers.Add(storageIdentifier);

        if (TryReadIdentifier(reference, referenceType, "DbIdentifier", out string? databaseIdentifier))
        {
            if (MatchesGenericType(referenceType, typeof(CosmosDbItemArtifactReference<>)))
            {
                UsedCosmosIdentifiers.Add(databaseIdentifier);
                RegisterCosmosSchema(databaseIdentifier, referenceType.GetGenericArguments()[0]);
            }
            else if (MatchesGenericType(referenceType, typeof(SqlRowArtifactReference<>)))
                UsedSqlIdentifiers.Add(databaseIdentifier);
        }
    }

    private DockerAzureResolutionSnapshot CreateResolutionSnapshot(IEnumerable<EnvComponentIdentifier> resolvedComponents, IReadOnlyCollection<ComponentContractBinding> contractBindings)
    {
        return new DockerAzureResolutionSnapshot(
            [.. resolvedComponents.Select(component => component.ToString()).OrderBy(x => x, StringComparer.Ordinal)],
            [.. UsedFunctionAppIdentifiers.OrderBy(x => x, StringComparer.Ordinal)],
            [.. UsedStorageIdentifiers.OrderBy(x => x, StringComparer.Ordinal)],
            [.. UsedCosmosIdentifiers.OrderBy(x => x, StringComparer.Ordinal)],
            [.. UsedSqlIdentifiers.OrderBy(x => x, StringComparer.Ordinal)],
            [.. UsedServiceBusIdentifiers.OrderBy(x => x, StringComparer.Ordinal)],
            [.. contractBindings.Select(FormatContractBinding).OrderBy(x => x, StringComparer.Ordinal)]);
    }

    private static string FormatContractBinding(ComponentContractBinding binding)
    {
        return $"{binding.ConsumerIdentity} <= {binding.ProviderIdentity} ({binding.Requirement.GetType().Name})";
    }

    private void ValidateFunctionAppRegistrations()
    {
        if (UsedFunctionAppIdentifiers.Count == 0)
            return;

        HashSet<string> configuredIdentifiers = [.. GetFunctionAppRegistrations().Select(x => x.Identifier)];
        string[] missingIdentifiers = [.. UsedFunctionAppIdentifiers.Where(identifier => !configuredIdentifiers.Contains(identifier)).OrderBy(identifier => identifier, StringComparer.Ordinal)];
        if (missingIdentifiers.Length > 0)
            throw new InvalidOperationException($"No Docker Function App registration was configured for: {string.Join(", ", missingIdentifiers)}.");
    }

    private void CaptureActivatedDefinitionUsage(IEnumerable<DockerAzureDefinitionMetadata> definitions)
    {
        foreach (DockerAzureDefinitionMetadata metadata in definitions)
        {
            switch (metadata.Definition)
            {
                case DockerStorageDefinition storage:
                    UsedStorageIdentifiers.Add(storage.Identifier);
                    break;
                case DockerCosmosDefinition cosmos:
                    UsedCosmosIdentifiers.Add(cosmos.Identifier);
                    if (cosmos.ModelType is not null)
                        RegisterCosmosSchema(cosmos.Identifier, cosmos.ModelType);
                    break;
                case DockerSqlDefinition sql:
                    UsedSqlIdentifiers.Add(sql.Identifier);
                    break;
                case DockerServiceBusDefinition serviceBus:
                    UsedServiceBusIdentifiers.Add(serviceBus.Identifier);
                    break;
                case DockerFunctionAppDefinition functionApp:
                    UsedFunctionAppIdentifiers.Add(functionApp.Identifier);
                    break;
            }
        }
    }

    private void RegisterCosmosSchema(string identifier, Type modelType)
    {
        string partitionKeyPath = CosmosModelSchemaResolver.ResolvePartitionKeyPath(modelType);
        if (CosmosPartitionKeyPaths.TryGetValue(identifier, out string? existingPath) && !string.Equals(existingPath, partitionKeyPath, StringComparison.Ordinal))
            throw new InvalidOperationException($"Cosmos identifier '{identifier}' was configured with conflicting partition key paths: '{existingPath}' and '{partitionKeyPath}'.");

        CosmosPartitionKeyPaths[identifier] = partitionKeyPath;
    }

    private static bool TryReadIdentifier(object instance, Type instanceType, string propertyName, out string value)
    {
        PropertyInfo? property = instanceType.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property?.GetValue(instance) is not null)
        {
            value = property.GetValue(instance)!.ToString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool MatchesGenericType(Type candidate, Type genericTypeDefinition)
    {
        return candidate.IsGenericType && candidate.GetGenericTypeDefinition() == genericTypeDefinition;
    }

    private void EnsureConfigPresent<TConfig>(ConfigStore<TConfig> store, string identifier, string componentName)
    {
        if (store.Snapshot().ContainsKey(identifier))
            return;

        if (_definitionState.TryGetDefaultConfig(typeof(TConfig), identifier, out object? defaultConfig) && defaultConfig is TConfig typedConfig)
        {
            store.AddConfig(identifier, typedConfig);
            return;
        }

        throw new InvalidOperationException($"{componentName} requires ConfigStore<{typeof(TConfig).Name}> for identifier '{identifier}', and no component default config was defined for that identifier.");
    }

    internal IReadOnlyCollection<string> GetUsedIdentifiersFor(Type configType)
    {
        if (configType == typeof(StorageAccountConfig))
            return UsedStorageIdentifiers;
        if (configType == typeof(CosmosContainerDbConfig))
            return UsedCosmosIdentifiers;
        if (configType == typeof(SqlDatabaseConfig))
            return UsedSqlIdentifiers;
        if (configType == typeof(ServiceBusConfig))
            return UsedServiceBusIdentifiers;
        if (configType == typeof(FunctionAppConfig))
            return UsedFunctionAppIdentifiers;

        throw new InvalidOperationException($"Unsupported config type '{configType.FullName}' for Docker Azure store resolution.");
    }

    protected override void OnRequirementResolved(EnvironmentRequirement requirement)
    {
        switch (requirement.ResourceKind)
        {
            case AzureEnvironmentResourceKinds.Storage:
                UsedStorageIdentifiers.Add(requirement.ResourceIdentifier);
                break;
            case AzureEnvironmentResourceKinds.Cosmos:
                UsedCosmosIdentifiers.Add(requirement.ResourceIdentifier);
                break;
            case AzureEnvironmentResourceKinds.Sql:
                UsedSqlIdentifiers.Add(requirement.ResourceIdentifier);
                break;
            case AzureEnvironmentResourceKinds.ServiceBus:
                UsedServiceBusIdentifiers.Add(requirement.ResourceIdentifier);
                break;
            case AzureEnvironmentResourceKinds.FunctionApp:
                UsedFunctionAppIdentifiers.Add(requirement.ResourceIdentifier);
                break;
            case AzureEnvironmentResourceKinds.LogicApp:
                throw new InvalidOperationException($"DockerAzureEnvironment no longer supports Logic App resource '{requirement.ResourceIdentifier}'. Use a live Azure-hosted Logic App instead of Docker container hosting.");
        }
    }

    internal DockerAzureEnvironment CloneDefinitions()
    {
        DockerAzureEnvironment clone = new();
        foreach (DockerAzureDefinition definition in _includedDefinitions.Values)
            clone.Include(definition);

        return clone;
    }

    private sealed class InlineFunctionAppDefinition<TFunctionApp>(FunctionAppIdentifier identifier, Action<DockerFunctionAppBuilder>? configure, string? image) : DockerFunctionAppDefinition<TFunctionApp>
    {
        public override FunctionAppIdentifier Identifier => identifier;

        public override string Image => string.IsNullOrWhiteSpace(image) ? base.Image : image!;

        protected override void Configure(DockerFunctionAppBuilder builder)
        {
            configure?.Invoke(builder);
        }
    }

    internal sealed record DockerAzureResolutionSnapshot(
        IReadOnlyCollection<string> ResolvedComponents,
        IReadOnlyCollection<string> FunctionApps,
        IReadOnlyCollection<string> Storage,
        IReadOnlyCollection<string> Cosmos,
        IReadOnlyCollection<string> Sql,
        IReadOnlyCollection<string> ServiceBus,
        IReadOnlyCollection<string> ContractBindings)
    {
        internal static DockerAzureResolutionSnapshot Empty { get; } = new([], [], [], [], [], [], []);

        internal IReadOnlyCollection<string> ToLogLines()
        {
            List<string> lines =
            [
                $"Docker Azure resolution: components [{JoinOrNone(ResolvedComponents)}]"
            ];

            AddIfAny(lines, "Function Apps", FunctionApps);
            AddIfAny(lines, "Storage", Storage);
            AddIfAny(lines, "Cosmos", Cosmos);
            AddIfAny(lines, "SQL", Sql);
            AddIfAny(lines, "Service Bus", ServiceBus);

            if (ContractBindings.Count > 0)
                lines.Add($"Docker Azure contracts: {JoinOrNone(ContractBindings)}");

            return lines;
        }

        private static void AddIfAny(List<string> lines, string label, IReadOnlyCollection<string> values)
        {
            if (values.Count > 0)
                lines.Add($"Docker Azure {label}: {JoinOrNone(values)}");
        }

        private static string JoinOrNone(IReadOnlyCollection<string> values)
            => values.Count == 0 ? "none" : string.Join(", ", values);
    }
}