using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Azure.Identifier;
using TestFramework.Container.Azure.Contracts;
using TestFramework.Core.Environment;

namespace TestFramework.Container.Azure;

public abstract class DockerAzureDefinition
{
    protected virtual void ConfigureDependencies(DockerAzureDependencyBuilder dependencies)
    {
    }

    protected virtual void ConfigureContracts(DockerAzureContractBuilder contracts)
    {
    }

    protected internal IReadOnlyCollection<ComponentDependency> GetDependencies()
    {
        DockerAzureDependencyBuilder dependencies = new();
        ConfigureDependencies(dependencies);
        return dependencies.Dependencies;
    }

    protected internal IReadOnlyCollection<IEnvironmentResourceContract> GetProvidedContracts()
    {
        DockerAzureContractBuilder contracts = new();
        ConfigureContracts(contracts);
        return contracts.Provides;
    }

    protected internal IReadOnlyCollection<IEnvironmentResourceContract> GetRequiredContracts()
    {
        DockerAzureContractBuilder contracts = new();
        ConfigureContracts(contracts);
        return contracts.Requires;
    }
}

public abstract class DockerAzureInfrastructureDefinition : DockerAzureDefinition
{
    public virtual string? AzuriteImage => null;

    public virtual string? CosmosDbImage => null;

    public virtual string? MsSqlImage => null;

    public virtual int? MsSqlMemoryLimitMb => null;

    public virtual string? ServiceBusImage => null;

    public virtual string? MsSqlPassword => null;

    public virtual string? ServiceBusTopologyConfigPath => null;

    protected virtual void ConfigureServiceBusTopology(DockerServiceBusTopologyBuilder builder)
    {
    }

    internal ServiceBusTopologySource? GetServiceBusTopologySource()
    {
        DockerServiceBusTopologyBuilder builder = new();
        ConfigureServiceBusTopology(builder);
        if (builder.HasNamespaces)
            return ServiceBusTopologySource.FromTopology(builder.Build());

        if (!string.IsNullOrWhiteSpace(ServiceBusTopologyConfigPath))
            return ServiceBusTopologySource.FromPath(ServiceBusTopologyConfigPath);

        return null;
    }
}

public abstract class DockerStorageDefinition : DockerAzureDefinition
{
    public abstract StorageAccountIdentifier Identifier { get; }

    protected virtual string? BlobContainerName => null;

    protected virtual string? QueueContainerName => null;

    protected virtual string? TableContainerName => null;

    internal StorageAccountConfig BuildConfig(string connectionString) => new()
    {
        ConnectionString = connectionString,
        BlobContainerName = BlobContainerName,
        QueueContainerName = QueueContainerName,
        TableContainerName = TableContainerName,
    };

    internal bool TryCreateDefaultConfig(out StorageAccountConfig config)
    {
        if (BlobContainerName is null && QueueContainerName is null && TableContainerName is null)
        {
            config = default!;
            return false;
        }

        config = BuildConfig(DockerAzureDefaults.PlaceholderConnectionString);
        return true;
    }
}

public abstract class DockerCosmosDefinition : DockerAzureDefinition
{
    public abstract CosmosContainerIdentifier Identifier { get; }

    public virtual Type? ModelType => null;

    protected virtual string? DatabaseName => null;

    protected virtual string? ContainerName => null;

    internal CosmosContainerDbConfig BuildConfig(string connectionString) => new()
    {
        ConnectionString = connectionString,
        DatabaseName = DatabaseName ?? throw new InvalidOperationException($"Override '{nameof(DatabaseName)}' on '{GetType().Name}' to provide a Cosmos database name."),
        ContainerName = ContainerName ?? throw new InvalidOperationException($"Override '{nameof(ContainerName)}' on '{GetType().Name}' to provide a Cosmos container name."),
    };

    internal bool TryCreateDefaultConfig(out CosmosContainerDbConfig config)
    {
        if (DatabaseName is null || ContainerName is null)
        {
            config = default!;
            return false;
        }

        config = BuildConfig(DockerAzureDefaults.PlaceholderConnectionString);
        return true;
    }
}

public abstract class DockerCosmosDefinition<TDocument> : DockerCosmosDefinition
{
    public sealed override Type ModelType => typeof(TDocument);
}

public abstract class DockerSqlDefinition : DockerAzureDefinition
{
    public abstract SqlDatabaseIdentifier Identifier { get; }

    protected virtual string? DatabaseName => null;

    protected virtual string? ContextType => null;

    internal SqlDatabaseConfig BuildConfig(string connectionString) => new()
    {
        ConnectionString = connectionString,
        DatabaseName = DatabaseName ?? throw new InvalidOperationException($"Override '{nameof(DatabaseName)}' on '{GetType().Name}' to provide a SQL database name."),
        ContextType = ContextType,
    };

    internal bool TryCreateDefaultConfig(out SqlDatabaseConfig config)
    {
        if (DatabaseName is null)
        {
            config = default!;
            return false;
        }

        config = BuildConfig(DockerAzureDefaults.PlaceholderConnectionString);
        return true;
    }
}

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

    public ServiceBusEndpointKind Kind { get; }

    public string EntityName { get; }

    public string? SubscriptionName { get; }

    public static DockerServiceBusEndpoint Queue(string queueName) => new(ServiceBusEndpointKind.Queue, queueName, null);

    public static DockerServiceBusEndpoint Topic(string topicName) => new(ServiceBusEndpointKind.Topic, topicName, null);

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

public abstract class DockerServiceBusDefinition : DockerAzureDefinition
{
    public abstract ServiceBusIdentifier Identifier { get; }

    public virtual string TopologyConfigPath => DockerAzureDefaults.ServiceBusTopologyConfigPath;

    protected virtual DockerServiceBusEndpoint? Endpoint => null;

    protected virtual bool RequiredSession => false;

    internal ServiceBusConfig BuildConfig(string connectionString)
    {
        DockerServiceBusEndpoint endpoint = Endpoint ?? throw new InvalidOperationException($"Override '{nameof(Endpoint)}' on '{GetType().Name}' to provide a Service Bus endpoint.");
        return endpoint.CreateConfig(connectionString, RequiredSession);
    }

    protected virtual void ConfigureServiceBusTopology(DockerServiceBusTopologyBuilder builder)
    {
    }

    internal bool TryCreateDefaultConfig(out ServiceBusConfig config)
    {
        if (Endpoint is null)
        {
            config = default!;
            return false;
        }

        config = BuildConfig(DockerAzureDefaults.PlaceholderConnectionString);
        return true;
    }

    internal ServiceBusTopologySource GetTopologySource()
    {
        DockerServiceBusTopologyBuilder builder = new();
        ConfigureServiceBusTopology(builder);
        return builder.HasNamespaces
            ? ServiceBusTopologySource.FromTopology(builder.Build())
            : ServiceBusTopologySource.FromPath(TopologyConfigPath);
    }
}

public abstract class DockerFunctionAppDefinition : DockerAzureDefinition
{
    private const string SynthesizedBaseUrl = "http://localhost/";
    private const string SynthesizedHostKey = "unused";

    public abstract FunctionAppIdentifier Identifier { get; }

    public virtual string Image => DockerAzureDefaults.FunctionAppImage;

    protected virtual FunctionAppConfig? CreateDefaultConfig() => new()
    {
        BaseUrl = SynthesizedBaseUrl,
        Code = SynthesizedHostKey,
        AdminCode = SynthesizedHostKey,
    };

    protected virtual void Configure(DockerFunctionAppBuilder builder)
    {
    }

    internal bool TryCreateDefaultConfig(out FunctionAppConfig config)
    {
        FunctionAppConfig? created = CreateDefaultConfig();
        if (created is null)
        {
            config = default!;
            return false;
        }

        config = created;
        return true;
    }

    internal FunctionAppDefinitionDescriptor CreateDescriptor()
    {
        DockerFunctionAppBuilder builder = new();
        Configure(builder);

        DockerFunctionAppRegistration registration = DockerFunctionAppRegistration.Create(
            Identifier,
            FunctionType,
            registrationBuilder =>
            {
                if (!string.Equals(Image, DockerAzureDefaults.FunctionAppImage, StringComparison.Ordinal))
                    registrationBuilder.WithImage(Image);

                foreach ((string key, string value) in builder.AdditionalSettings)
                    registrationBuilder.WithAppSetting(key, value);
            });

        return new FunctionAppDefinitionDescriptor(registration, builder.ServiceBusTopologySources, builder.Dependencies, builder.ResourceBindings);
    }

    internal abstract Type FunctionType { get; }
}

public abstract class DockerFunctionAppDefinition<TFunctionApp> : DockerFunctionAppDefinition
{
    internal sealed override Type FunctionType => typeof(TFunctionApp);
}

public sealed class DockerAzureDependencyBuilder
{
    private readonly Dictionary<Type, ComponentDependency> _dependencies = [];

    internal IReadOnlyCollection<ComponentDependency> Dependencies => _dependencies.Values;

    public DockerAzureDependencyBuilder Include<TDefinition>(DependencyOwnership ownership = DependencyOwnership.Shared)
        where TDefinition : DockerAzureDefinition, new()
    {
        Type definitionType = typeof(TDefinition);
        ComponentDependency dependency = new(definitionType, ownership);

        if (_dependencies.TryGetValue(definitionType, out ComponentDependency? existing) && existing is not null && existing.Ownership != ownership)
            throw new InvalidOperationException($"Dependency '{definitionType.FullName}' was configured with conflicting ownership values.");

        _dependencies[definitionType] = dependency;
        return this;
    }
}

public sealed class DockerFunctionAppBuilder
{
    private readonly Dictionary<Type, ComponentDependency> _dependencies = [];

    internal Dictionary<string, string> AdditionalSettings { get; } = [];
    internal List<ServiceBusTopologySource> ServiceBusTopologySources { get; } = [];
    internal List<FunctionAppResourceBinding> ResourceBindings { get; } = [];
    internal IReadOnlyCollection<ComponentDependency> Dependencies => _dependencies.Values;

    /// <summary>
    /// Binds a storage definition into the Function App container settings.
    /// </summary>
    /// <param name="connectionSettingName">Setting name for the storage connection string consumed by the app.</param>
    /// <param name="hostStorageSettingName">Setting name for the Functions host storage connection string.</param>
    /// <param name="tableNameSettingName">Setting name for the table name. Defaults to <c>StorageTableName</c>; set <see langword="null"/> to suppress table-name injection.</param>
    /// <param name="ownership">Whether the dependency may be shared with other definitions.</param>
    public DockerFunctionAppBuilder UseStorage<TStorage>(
        string connectionSettingName = "StorageAccountConnectionString",
        string hostStorageSettingName = "AzureWebJobsStorage",
        string? tableNameSettingName = "StorageTableName",
        DependencyOwnership ownership = DependencyOwnership.Shared)
        where TStorage : DockerStorageDefinition, new()
    {
        TStorage definition = new();
        AddDependency(typeof(TStorage), ownership);
        ReplaceResourceBinding(new FunctionAppResourceBinding(
            FunctionAppResourceBindingKind.Storage,
            definition.Identifier,
            connectionSettingName,
            hostStorageSettingName,
            tableNameSettingName));
        return this;
    }

    public DockerFunctionAppBuilder UseCosmos<TCosmos>(
        string connectionSettingName = "CosmosDbConnectionString",
        string databaseSettingName = "CosmosDatabaseName",
        string containerSettingName = "CosmosContainerName",
        DependencyOwnership ownership = DependencyOwnership.Shared)
        where TCosmos : DockerCosmosDefinition, new()
    {
        TCosmos definition = new();
        AddDependency(typeof(TCosmos), ownership);
        ReplaceResourceBinding(new FunctionAppResourceBinding(
            FunctionAppResourceBindingKind.Cosmos,
            definition.Identifier,
            connectionSettingName,
            databaseSettingName,
            containerSettingName));
        return this;
    }

    public DockerFunctionAppBuilder UseServiceBusTrigger<TServiceBus>(
        string connectionSettingName = "ServiceBusTriggerConnection",
        string entitySettingName = "ServiceBusTriggerTopicName",
        string? subscriptionSettingName = "ServiceBusTriggerSubscriptionName",
        DependencyOwnership ownership = DependencyOwnership.Shared)
        where TServiceBus : DockerServiceBusDefinition, new()
    {
        TServiceBus definition = new();
        AddDependency(typeof(TServiceBus), ownership);
        ReplaceResourceBinding(new FunctionAppResourceBinding(
            FunctionAppResourceBindingKind.ServiceBusTrigger,
            definition.Identifier,
            connectionSettingName,
            entitySettingName,
            subscriptionSettingName));
        AddServiceBusTopologySource(definition.GetTopologySource());
        return this;
    }

    public DockerFunctionAppBuilder UseServiceBusReply<TServiceBus>(
        string connectionSettingName = "ServiceBusReplyConnectionString",
        string entitySettingName = "ServiceBusReplyTopicName",
        DependencyOwnership ownership = DependencyOwnership.Shared)
        where TServiceBus : DockerServiceBusDefinition, new()
    {
        TServiceBus definition = new();
        AddDependency(typeof(TServiceBus), ownership);
        ReplaceResourceBinding(new FunctionAppResourceBinding(
            FunctionAppResourceBindingKind.ServiceBusReply,
            definition.Identifier,
            connectionSettingName,
            entitySettingName));
        AddServiceBusTopologySource(definition.GetTopologySource());
        return this;
    }

    public DockerFunctionAppBuilder WithAppSetting(string key, string value)
    {
        AdditionalSettings[key] = value;
        return this;
    }

    private void AddDependency(Type dependencyType, DependencyOwnership ownership)
    {
        ComponentDependency dependency = new(dependencyType, ownership);

        if (_dependencies.TryGetValue(dependencyType, out ComponentDependency? existing) && existing is not null && existing.Ownership != ownership)
            throw new InvalidOperationException($"Dependency '{dependencyType.FullName}' was configured with conflicting ownership values.");

        _dependencies[dependencyType] = dependency;
    }

    private void ReplaceResourceBinding(FunctionAppResourceBinding binding)
    {
        ResourceBindings.RemoveAll(existing => existing.Kind == binding.Kind);
        ResourceBindings.Add(binding);
    }

    private void AddServiceBusTopologySource(ServiceBusTopologySource source)
    {
        if (source.IsPath && string.Equals(source.ConfigPath, DockerAzureDefaults.ServiceBusTopologyConfigPath, StringComparison.OrdinalIgnoreCase))
            return;

        if (ServiceBusTopologySources.Any(existing => existing.SemanticallyEquals(source)))
            return;

        ServiceBusTopologySources.Add(source);
    }
}

public static class DockerAzureDefaults
{
    public const string FunctionAppImage = "mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated8.0";
    public const string PlaceholderConnectionString = "placeholder://container-managed";
    public const string MsSqlImage = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";
    public const int MsSqlMemoryLimitMb = 1536;
    public const string AzuriteImage = "mcr.microsoft.com/azure-storage/azurite:3.33.0";
    public const string AzuriteAccountName = "devstoreaccount1";
    public const string AzuriteAccountKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    public const string CosmosDbImage = "mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview";
    public const string CosmosDbEmulatorAccountKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
    public const string ServiceBusImage = "mcr.microsoft.com/azure-messaging/servicebus-emulator:latest";
    public const string MsSqlPassword = "TestFramework_Container1!";
    public static readonly string ServiceBusTopologyConfigPath = Path.Combine("Configurations", "ServiceBus", "config.json");
    public static readonly string DefaultAzuriteConnectionString = $"DefaultEndpointsProtocol=http;AccountName={AzuriteAccountName};AccountKey={AzuriteAccountKey};BlobEndpoint=http://127.0.0.1:10000/{AzuriteAccountName};QueueEndpoint=http://127.0.0.1:10001/{AzuriteAccountName};TableEndpoint=http://127.0.0.1:10002/{AzuriteAccountName};";
}

internal sealed record FunctionAppDefinitionDescriptor(
    DockerFunctionAppRegistration Registration,
    IReadOnlyCollection<ServiceBusTopologySource> ServiceBusTopologySources,
    IReadOnlyCollection<ComponentDependency> Dependencies,
    IReadOnlyCollection<FunctionAppResourceBinding> ResourceBindings);

internal enum FunctionAppResourceBindingKind
{
    Storage,
    Cosmos,
    ServiceBusTrigger,
    ServiceBusReply,
}

internal sealed record FunctionAppResourceBinding(
    FunctionAppResourceBindingKind Kind,
    string ResourceIdentifier,
    string PrimarySettingName,
    string? SecondarySettingName = null,
    string? TertiarySettingName = null);

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
                ServiceBusImage = ResolveOverride(ServiceBusImage, infrastructure.ServiceBusImage, nameof(DockerAzureInfrastructureDefinition.ServiceBusImage));
                MsSqlPassword = ResolveOverride(MsSqlPassword, infrastructure.MsSqlPassword, nameof(DockerAzureInfrastructureDefinition.MsSqlPassword));
                if (infrastructure.GetServiceBusTopologySource() is ServiceBusTopologySource infrastructureTopologySource)
                    SetServiceBusTopologySource(infrastructureTopologySource);
                break;
            case DockerStorageDefinition:
                break;
            case DockerCosmosDefinition:
                break;
            case DockerSqlDefinition:
                break;
            case DockerServiceBusDefinition serviceBus:
                ServiceBusTopologySource serviceBusTopologySource = serviceBus.GetTopologySource();
                if (!serviceBusTopologySource.IsPath || !string.Equals(serviceBusTopologySource.ConfigPath, DockerAzureDefaults.ServiceBusTopologyConfigPath, StringComparison.OrdinalIgnoreCase))
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
                throw new InvalidOperationException($"Unsupported Docker Azure definition type '{definition.GetType().FullName}'.");
        }
    }

    private void AddDependencyDefinition(Type dependencyType, Type ownerType)
    {
        if (!typeof(DockerAzureDefinition).IsAssignableFrom(dependencyType))
            throw new InvalidOperationException($"Dependency type '{dependencyType.FullName}' declared by '{ownerType.FullName}' is not a Docker Azure definition.");

        if (dependencyType.IsAbstract)
            throw new InvalidOperationException($"Dependency type '{dependencyType.FullName}' declared by '{ownerType.FullName}' must be a concrete Docker Azure definition.");

        if (Activator.CreateInstance(dependencyType) is not DockerAzureDefinition dependencyDefinition)
            throw new InvalidOperationException($"Dependency type '{dependencyType.FullName}' declared by '{ownerType.FullName}' could not be constructed.");

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

        throw new InvalidOperationException($"No Docker Function App registration was configured for identifier '{identifier}'.");
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
            case DockerSqlDefinition sql when configType == typeof(SqlDatabaseConfig) && sql.TryCreateDefaultConfig(out SqlDatabaseConfig sqlConfig):
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
            if (existing.Registration.FunctionType != descriptor.Registration.FunctionType)
                throw new InvalidOperationException($"Docker Function App identifier '{identifier}' was configured for multiple function types.");

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
            throw new InvalidOperationException($"Multiple Service Bus topology sources were configured: {ServiceBusTopologySource.Describe()} and {source.Describe()}.");
    }

    private static string? ResolveOverride(string? current, string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return current;

        if (current is null)
            return value;

        if (!string.Equals(current, value, StringComparison.Ordinal))
            throw new InvalidOperationException($"Multiple values were configured for {propertyName}: '{current}' and '{value}'.");

        return current;
    }

    private static int? ResolveOverride(int? current, int? value, string propertyName)
    {
        if (value is null)
            return current;

        if (current is null)
            return value;

        if (current != value)
            throw new InvalidOperationException($"Multiple values were configured for {propertyName}: '{current}' and '{value}'.");

        return current;
    }

    private ComponentGraphNode CreateGraphNode(DockerAzureDefinitionMetadata metadata)
    {
        ComponentGraphDependency[] dependencies = [.. metadata.Dependencies.Select(dependency =>
            new ComponentGraphDependency(
                dependency.ComponentType,
                _definitionMetadata[dependency.ComponentType].RealizedIdentity,
                dependency.Ownership))];

        return new ComponentGraphNode(
            metadata.DefinitionType,
            metadata.RealizedIdentity,
            dependencies,
            metadata.Provides,
            metadata.Requires);
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
        throw new InvalidOperationException($"Unsupported config type '{configType.FullName}' for Docker Azure default config lookup.");
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
                throw new InvalidOperationException($"Dependency '{dependency.ComponentType.FullName}' was configured with conflicting ownership values.");
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
            DockerSqlDefinition sql => $"sql:{sql.Identifier}",
            DockerServiceBusDefinition serviceBus => $"servicebus:{serviceBus.Identifier}",
            DockerFunctionAppDefinition functionApp => $"functionapp:{functionApp.Identifier}",
            DockerAzureInfrastructureDefinition infrastructure => $"infrastructure:{infrastructure.GetType().FullName}",
            _ => $"definition:{definition.GetType().FullName}"
        };
    }
}

internal sealed record DockerAzureDefinitionMetadata(
    DockerAzureDefinition Definition,
    Type DefinitionType,
    string RealizedIdentity,
    IReadOnlyCollection<ComponentDependency> Dependencies,
    IReadOnlyCollection<IEnvironmentResourceContract> Provides,
    IReadOnlyCollection<IEnvironmentResourceContract> Requires);
