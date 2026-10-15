using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Azure.Identifier;
using TestFramework.Container.Azure.Contracts;
using TestFramework.Container.Sources;
using TestFramework.Core.Environment;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Azure;

/// <summary>
/// Base of every definition a <see cref="DockerAzureEnvironment"/> includes: one containerised resource or
/// infrastructure setting, plus the definitions and contracts it depends on.
/// </summary>
public abstract class DockerAzureDefinition
{
    /// <summary>
    /// Declares the definitions this one depends on. They are included with it and started whenever it is used.
    /// The default declares none.
    /// </summary>
    /// <param name="dependencies">Collects the dependencies.</param>
    protected virtual void ConfigureDependencies(DockerAzureDependencyBuilder dependencies)
    {
    }

    /// <summary>
    /// Declares the resource contracts this definition provides and requires. Each requirement is bound to a
    /// matching provider (see <see cref="DockerAzureContractMatcher"/>) when the environment resolves, and the
    /// provider is started with the definition that requires it. The default declares none.
    /// </summary>
    /// <param name="contracts">Collects the contracts.</param>
    protected virtual void ConfigureContracts(DockerAzureContractBuilder contracts)
    {
    }

    /// <summary>
    /// Runs <see cref="ConfigureDependencies"/> on a fresh builder and returns what it declared.
    /// </summary>
    /// <returns>The declared dependencies, one per definition type.</returns>
    protected internal IReadOnlyCollection<ComponentDependency> GetDependencies()
    {
        DockerAzureDependencyBuilder dependencies = new();
        ConfigureDependencies(dependencies);
        return dependencies.Dependencies;
    }

    /// <summary>
    /// Runs <see cref="ConfigureContracts"/> on a fresh builder and returns the contracts it provides.
    /// </summary>
    /// <returns>The provided contracts.</returns>
    protected internal IReadOnlyCollection<IEnvironmentResourceContract> GetProvidedContracts()
    {
        DockerAzureContractBuilder contracts = new();
        ConfigureContracts(contracts);
        return contracts.Provides;
    }

    /// <summary>
    /// Runs <see cref="ConfigureContracts"/> on a fresh builder and returns the contracts it requires.
    /// </summary>
    /// <returns>The required contracts.</returns>
    protected internal IReadOnlyCollection<IEnvironmentResourceContract> GetRequiredContracts()
    {
        DockerAzureContractBuilder contracts = new();
        ConfigureContracts(contracts);
        return contracts.Requires;
    }
}

/// <summary>
/// Overrides environment-wide container settings: images, SQL Server limits and password, and the Service Bus
/// emulator topology.
/// </summary>
/// <remarks>
/// Every property left <c>null</c> keeps the matching <see cref="DockerAzureDefaults"/> value. Several
/// infrastructure definitions may be included; two that set the same property to different values fail when
/// the second is included.
/// </remarks>
public abstract class DockerAzureInfrastructureDefinition : DockerAzureDefinition
{
    /// <summary>
    /// The Azurite image to start instead of <see cref="DockerAzureDefaults.AzuriteImage"/>.
    /// </summary>
    public virtual string? AzuriteImage => null;

    /// <summary>
    /// The Cosmos DB emulator image to start instead of <see cref="DockerAzureDefaults.CosmosDbImage"/>.
    /// </summary>
    public virtual string? CosmosDbImage => null;

    /// <summary>
    /// The SQL Server image to start instead of <see cref="DockerAzureDefaults.MsSqlImage"/>.
    /// </summary>
    public virtual string? MsSqlImage => null;

    /// <summary>
    /// The SQL Server container's memory limit in megabytes, instead of <see cref="DockerAzureDefaults.MsSqlMemoryLimitMb"/>.
    /// </summary>
    public virtual int? MsSqlMemoryLimitMb => null;

    /// <summary>
    /// How long to wait for the started SQL Server to answer.
    /// </summary>
    /// <remarks>
    /// A first start on a cold image, a loaded CI agent or a machine that is also building can all push
    /// SQL Server past the default. The Web package allows ninety seconds for the same pinned image.
    /// </remarks>
    public virtual TimeSpan? MsSqlReadinessTimeout => null;

    /// <summary>
    /// The Service Bus emulator image to start instead of <see cref="DockerAzureDefaults.ServiceBusImage"/>.
    /// </summary>
    public virtual string? ServiceBusImage => null;

    /// <summary>
    /// A fixed <c>sa</c> password for the SQL Server container. When <c>null</c>, each SQL Server container
    /// generates its own.
    /// </summary>
    public virtual string? MsSqlPassword => null;

    /// <summary>
    /// An emulator config file naming the Service Bus queues and topics. Used only when
    /// <see cref="ConfigureServiceBusTopology"/> declares no namespace.
    /// </summary>
    public virtual string? ServiceBusTopologyConfigPath => null;

    /// <summary>
    /// Declares the Service Bus emulator topology in code. A topology declared here takes precedence over
    /// <see cref="ServiceBusTopologyConfigPath"/>. The default declares nothing.
    /// </summary>
    /// <param name="builder">Collects the namespaces.</param>
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

/// <summary>
/// A storage account served by the Azurite container.
/// </summary>
/// <remarks>
/// When at least one container name is set, the definition declares a storage account configuration for
/// <see cref="Identifier"/> to the run; its connection string is replaced by the address Azurite publishes.
/// </remarks>
public abstract class DockerStorageDefinition : DockerAzureDefinition
{
    /// <summary>
    /// The storage account identifier steps and configuration refer to.
    /// </summary>
    public abstract StorageAccountIdentifier Identifier { get; }

    /// <summary>
    /// The blob container name declared in the storage account configuration, or <c>null</c> for none.
    /// </summary>
    protected virtual string? BlobContainerName => null;

    /// <summary>
    /// The queue name declared in the storage account configuration, or <c>null</c> for none.
    /// </summary>
    protected virtual string? QueueContainerName => null;

    /// <summary>
    /// The table name declared in the storage account configuration, or <c>null</c> for none.
    /// </summary>
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

/// <summary>
/// A Cosmos DB container served by the Cosmos DB emulator container.
/// </summary>
/// <remarks>
/// When both <see cref="DatabaseName"/> and <see cref="ContainerName"/> are set, the definition declares a Cosmos
/// configuration for <see cref="Identifier"/> to the run; its connection string is replaced by the address the
/// emulator publishes.
/// </remarks>
public abstract class DockerCosmosDefinition : DockerAzureDefinition
{
    /// <summary>
    /// The Cosmos container identifier steps and configuration refer to.
    /// </summary>
    public abstract CosmosContainerIdentifier Identifier { get; }

    /// <summary>
    /// The document type stored in the container. When set, the container's partition key path is read from it.
    /// </summary>
    public virtual Type? ModelType => null;

    /// <summary>
    /// The Cosmos database name declared in the configuration, or <c>null</c> to declare no configuration.
    /// </summary>
    protected virtual string? DatabaseName => null;

    /// <summary>
    /// The Cosmos container name declared in the configuration, or <c>null</c> to declare no configuration.
    /// </summary>
    protected virtual string? ContainerName => null;

    internal CosmosContainerDbConfig BuildConfig(string connectionString) => new()
    {
        ConnectionString = connectionString,
        DatabaseName = DatabaseName ?? throw new FrameworkConfigurationException($"Override '{nameof(DatabaseName)}' on '{GetType().Name}' to provide a Cosmos database name."),
        ContainerName = ContainerName ?? throw new FrameworkConfigurationException($"Override '{nameof(ContainerName)}' on '{GetType().Name}' to provide a Cosmos container name."),
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

/// <summary>
/// A <see cref="DockerCosmosDefinition"/> whose document type is <typeparamref name="TDocument"/>.
/// </summary>
/// <typeparam name="TDocument">The document type; the container's partition key path is read from it.</typeparam>
public abstract class DockerCosmosDefinition<TDocument> : DockerCosmosDefinition
{
    /// <summary>
    /// Always <typeparamref name="TDocument"/>.
    /// </summary>
    public sealed override Type ModelType => typeof(TDocument);
}

/// <summary>
/// A SQL database served by the SQL Server container.
/// </summary>
/// <remarks>
/// When <see cref="DatabaseName"/> is set, the definition declares a SQL database configuration for
/// <see cref="Identifier"/> to the run; its connection string is replaced by the address SQL Server publishes.
/// </remarks>
public abstract class DockerAzureSqlDefinition : DockerAzureDefinition
{
    /// <summary>
    /// The SQL database identifier steps and configuration refer to.
    /// </summary>
    public abstract SqlDatabaseIdentifier Identifier { get; }

    /// <summary>
    /// The database name declared in the configuration, or <c>null</c> to declare no configuration.
    /// </summary>
    protected virtual string? DatabaseName => null;

    internal SqlDatabaseConfig BuildConfig(string connectionString) => new()
    {
        ConnectionString = connectionString,
        DatabaseName = DatabaseName ?? throw new FrameworkConfigurationException($"Override '{nameof(DatabaseName)}' on '{GetType().Name}' to provide a SQL database name."),
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

/// <summary>
/// A Service Bus namespace served by the Service Bus emulator container.
/// </summary>
/// <remarks>
/// The definition always declares a Service Bus configuration for <see cref="Identifier"/> to the run; its
/// connection string is replaced by the address the emulator publishes. A run that uses Service Bus needs a
/// topology, from <see cref="ConfigureServiceBusTopology"/> or <see cref="TopologyConfigPath"/>.
/// </remarks>
public abstract class DockerServiceBusDefinition : DockerAzureDefinition
{
    /// <summary>
    /// The Service Bus identifier steps and configuration refer to.
    /// </summary>
    public abstract ServiceBusIdentifier Identifier { get; }

    /// <summary>
    /// An emulator config file naming the queues and topics, for a definition that does not describe them through
    /// <see cref="ConfigureServiceBusTopology"/>.
    /// </summary>
    /// <remarks>
    /// No default. There used to be one - a file this package copied to its own build output and never shipped - so
    /// a definition that described nothing passed resolution and failed only once SQL Server had started, in a
    /// consumer's output where no such file existed.
    /// </remarks>
    public virtual string? TopologyConfigPath => null;

    /// <summary>
    /// The <see cref="ServiceBusConfig.RequiredSession"/> value of every configuration this definition builds.
    /// Defaults to <c>false</c>.
    /// </summary>
    protected virtual bool RequiredSession => false;

    /// <summary>
    /// Builds a Service Bus configuration for <paramref name="endpoint"/>, with <see cref="RequiredSession"/> applied.
    /// </summary>
    /// <param name="connectionString">The connection string to put in the configuration.</param>
    /// <param name="endpoint">The queue, topic or topic subscription the configuration names.</param>
    /// <returns>The configuration.</returns>
    protected ServiceBusConfig BuildConfig(string connectionString, DockerServiceBusEndpoint endpoint)
        => endpoint.CreateConfig(connectionString, RequiredSession);

    /// <summary>
    /// The configuration this definition declares to the run, typically built with <see cref="BuildConfig"/>.
    /// </summary>
    /// <remarks>
    /// The default returns <c>null</c>, which declares a configuration naming no entity. An empty connection string
    /// is replaced by <see cref="DockerAzureDefaults.PlaceholderConnectionString"/>.
    /// </remarks>
    /// <returns>The configuration, or <c>null</c> for one naming no entity.</returns>
    protected virtual ServiceBusConfig? CreateDefaultConfig()
    {
        return null;
    }

    /// <summary>
    /// Declares the emulator's queues and topics in code. A topology declared here takes precedence over
    /// <see cref="TopologyConfigPath"/>. The default declares nothing.
    /// </summary>
    /// <param name="builder">Collects the namespaces.</param>
    protected virtual void ConfigureServiceBusTopology(DockerServiceBusTopologyBuilder builder)
    {
    }

    internal bool TryCreateDefaultConfig(out ServiceBusConfig config)
    {
        config = CreateDefaultConfig() ?? new ServiceBusConfig
        {
            ConnectionString = DockerAzureDefaults.PlaceholderConnectionString,
            QueueName = null,
            TopicName = null,
            SubscriptionName = null,
            RequiredSession = RequiredSession,
        };

        if (string.IsNullOrWhiteSpace(config.ConnectionString))
            config = config with { ConnectionString = DockerAzureDefaults.PlaceholderConnectionString };

        return true;
    }

    /// <summary>
    /// The topology this definition describes, or <see langword="null"/> when it describes none.
    /// </summary>
    internal ServiceBusTopologySource? GetTopologySource()
    {
        DockerServiceBusTopologyBuilder builder = new();
        ConfigureServiceBusTopology(builder);
        if (builder.HasNamespaces)
            return ServiceBusTopologySource.FromTopology(builder.Build());

        return string.IsNullOrWhiteSpace(TopologyConfigPath) ? null : ServiceBusTopologySource.FromPath(TopologyConfigPath);
    }
}

/// <summary>
/// A Function App run in a Functions host container, with its payload mounted from a declared
/// <see cref="Source"/>.
/// </summary>
public abstract class DockerFunctionAppDefinition : DockerAzureDefinition
{
    private const string SynthesizedBaseUrl = "http://localhost/";
    private const string SynthesizedHostKey = "unused";

    /// <summary>
    /// The Function App identifier steps and configuration refer to.
    /// </summary>
    public abstract FunctionAppIdentifier Identifier { get; }

    /// <summary>
    /// The Functions host image the payload is mounted into.
    /// </summary>
    /// <remarks>
    /// Left at <see cref="DockerAzureDefaults.FunctionAppImage"/>, the image is derived from the payload's target
    /// framework (see <see cref="DockerAzureDefaults.FunctionAppImageFor(string)"/>). Any other value is used as given.
    /// </remarks>
    public virtual string Image => DockerAzureDefaults.FunctionAppImage;

    /// <summary>
    /// Where the payload mounted into the Functions host comes from.
    /// </summary>
    /// <remarks>
    /// There is no default: the payload is declared, not discovered. Because it is mounted into the
    /// Functions host image rather than run as an image of its own, a project source has to publish
    /// on the host: <c>ContainerSource.Project("../App/App.csproj").BuiltOnHost()</c>.
    /// </remarks>
    public virtual ContainerSource Source => throw new FrameworkConfigurationException(
        $"Function App '{Identifier}' declares no container source, so there is no payload to mount.")
    {
        RecoverySteps = ["Override Source with ContainerSource.Project(\"...\").BuiltOnHost() or ContainerSource.Directory(\"...\")."],
    };

    /// <summary>
    /// The Function App configuration this definition declares to the run.
    /// </summary>
    /// <remarks>
    /// The default carries a placeholder base URL, which the address the container publishes replaces, and host
    /// keys set to a fixed unused value. Return <c>null</c> to declare no configuration.
    /// </remarks>
    /// <returns>The configuration, or <c>null</c> for none.</returns>
    protected virtual FunctionAppConfig? CreateDefaultConfig() => new()
    {
        BaseUrl = SynthesizedBaseUrl,
        Code = SynthesizedHostKey,
        AdminCode = SynthesizedHostKey,
    };

    /// <summary>
    /// Binds the Function App to storage, Cosmos and Service Bus definitions and adds app settings. Each binding
    /// also makes the bound definition a dependency. The default binds nothing.
    /// </summary>
    /// <param name="builder">Collects the bindings and settings.</param>
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
            Source,
            registrationBuilder =>
            {
                if (!string.Equals(Image, DockerAzureDefaults.FunctionAppImage, StringComparison.Ordinal))
                    registrationBuilder.WithImage(Image);

                foreach ((string key, string value) in builder.AdditionalSettings)
                    registrationBuilder.WithAppSetting(key, value);
            });

        return new FunctionAppDefinitionDescriptor(GetType(), registration, builder.ServiceBusTopologySources, builder.Dependencies, builder.ResourceBindings);
    }
}

/// <summary>
/// Collects the definitions a <see cref="DockerAzureDefinition"/> depends on.
/// </summary>
public sealed class DockerAzureDependencyBuilder
{
    private readonly Dictionary<Type, ComponentDependency> _dependencies = [];

    internal IReadOnlyCollection<ComponentDependency> Dependencies => _dependencies.Values;

    /// <summary>
    /// Declares a shared dependency on <typeparamref name="TDefinition"/>.
    /// </summary>
    /// <remarks>Uses <see cref="DependencyOwnership.Shared"/> for <c>ownership</c>; a longer overload states it.</remarks>
    /// <typeparam name="TDefinition">The definition depended on.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public DockerAzureDependencyBuilder Include<TDefinition>()
        where TDefinition : DockerAzureDefinition, new()
        => this.Include<TDefinition>(DependencyOwnership.Shared);

    /// <summary>
    /// Declares a dependency on <typeparamref name="TDefinition"/>. Declaring the same type again is allowed only
    /// with the same <paramref name="ownership"/>.
    /// </summary>
    /// <typeparam name="TDefinition">The definition depended on.</typeparam>
    /// <param name="ownership">Whether the dependency may be shared with other dependents.</param>
    /// <returns>This builder for chaining.</returns>
    /// <exception cref="FrameworkConfigurationException">The type was already declared with a different ownership.</exception>
    public DockerAzureDependencyBuilder Include<TDefinition>(DependencyOwnership ownership)
        where TDefinition : DockerAzureDefinition, new()
    {
        Type definitionType = typeof(TDefinition);
        ComponentDependency dependency = new() { ComponentType = definitionType, Ownership = ownership };

        if (_dependencies.TryGetValue(definitionType, out ComponentDependency? existing) && existing is not null && existing.Ownership != ownership)
            throw new FrameworkConfigurationException($"Dependency '{definitionType.FullName}' was configured with conflicting ownership values.");

        _dependencies[definitionType] = dependency;
        return this;
    }
}

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

/// <summary>
/// The images, credentials and limits the Docker Azure environment uses when no definition overrides them.
/// </summary>
public static class DockerAzureDefaults
{
    /// <summary>
    /// The Functions host image used when the payload's framework cannot be worked out.
    /// </summary>
    /// <remarks>
    /// A fallback rather than a choice. The Functions host image carries exactly one .NET runtime, so
    /// an application built for a different one cannot start in it -- the worker exits with code 150
    /// and the only trace is inside the container log. Prefer
    /// <see cref="FunctionAppImageFor(string)"/>, which reads the tag off the payload.
    /// </remarks>
    public const string FunctionAppImage = "mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated8.0";

    /// <summary>
    /// The repository the isolated Functions host images come from.
    /// </summary>
    public const string FunctionAppImageRepository = "mcr.microsoft.com/azure-functions/dotnet-isolated";

    /// <summary>
    /// Returns the Functions host image that can run an application built for this framework.
    /// </summary>
    /// <param name="targetFramework">The framework the payload was built for, for example <c>net8.0</c>.</param>
    /// <returns>The image reference, or <see langword="null"/> when the framework is not one this can map.</returns>
    /// <remarks>
    /// The host image bundles one runtime and nothing else, so the tag has to follow the payload. It
    /// used to be pinned to 8.0 whatever the application targeted, which meant a Function App built
    /// for anything newer started, failed to launch its worker, and then simply never answered until
    /// the readiness timeout expired four minutes later.
    /// </remarks>
    public static string? FunctionAppImageFor(string? targetFramework)
    {
        if (string.IsNullOrWhiteSpace(targetFramework)
            || !targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string version = targetFramework[3..];

        // Only the shapes the isolated images are published under: 'netX.Y'. A framework identifier
        // such as net48 or netstandard2.0 is not something a Functions host image exists for.
        return version.Contains('.', StringComparison.Ordinal) && char.IsAsciiDigit(version[0])
            ? $"{FunctionAppImageRepository}:4-dotnet-isolated{version}"
            : null;
    }

    /// <summary>
    /// The connection string a definition's declared configuration carries until its container starts; the address
    /// the container publishes while it starts takes its place.
    /// </summary>
    public const string PlaceholderConnectionString = "placeholder://container-managed";

    /// <summary>
    /// The SQL Server image started for declared SQL databases and as the Service Bus emulator's backing store.
    /// </summary>
    public const string MsSqlImage = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";

    /// <summary>
    /// The SQL Server container's memory limit in megabytes.
    /// </summary>
    public const int MsSqlMemoryLimitMb = 1536;

    /// <summary>
    /// How long to wait for the started SQL Server to answer.
    /// </summary>
    /// <remarks>
    /// The same as the Web package allows for the same pinned image. Thirty seconds was enough on a warm
    /// machine and nowhere near enough on a first start or a loaded CI agent. Override it with
    /// <see cref="DockerAzureInfrastructureDefinition.MsSqlReadinessTimeout"/>.
    /// </remarks>
    public static readonly TimeSpan MsSqlReadinessTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// The Azurite image started for declared storage accounts.
    /// </summary>
    public const string AzuriteImage = "mcr.microsoft.com/azure-storage/azurite:3.33.0";

    /// <summary>
    /// Azurite's well-known development account name.
    /// </summary>
    public const string AzuriteAccountName = "devstoreaccount1";

    /// <summary>
    /// Azurite's well-known development account key.
    /// </summary>
    public const string AzuriteAccountKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    /// <summary>
    /// The Cosmos DB emulator image started for declared Cosmos containers.
    /// </summary>
    /// <remarks>
    /// Every other image here is pinned; this one cannot be. The Linux Cosmos emulator ships only
    /// moving tags — <c>vnext-preview</c> is the one Microsoft documents — so a pull can change the
    /// emulator between runs with nothing in the repository to show for it. Pin it with
    /// <see cref="DockerAzureInfrastructureDefinition.CosmosDbImage"/>, by tag or by digest, when a run
    /// has to be reproducible over time.
    /// </remarks>
    public const string CosmosDbImage = "mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview";

    /// <summary>
    /// How long the one-off bootstrap of a persistent container stack may take.
    /// </summary>
    /// <remarks>
    /// Generous because it is a ceiling, not a delay: nothing waits for it when startup is quick, and
    /// the cost of setting it too low is a green suite that fails for no reason. Two minutes was too
    /// low. A persistent slice can be a Docker network, Azurite, the Cosmos emulator, SQL Server, the
    /// Service Bus emulator - which only starts once SQL is serving - and one or more Function App
    /// hosts. That is minutes of legitimate work on a warm machine and more on a CI runner pulling
    /// images for the first time; measured at about two and a half minutes warm locally, and it
    /// overran two minutes on a hosted runner, where the timeout cancelled the token mid-start and
    /// surfaced as a container readiness failure rather than as the timeout it was. The one value for
    /// every road into a hosted stack: the fixture and <c>DockerAzureHostedEnvironment.StartAsync</c>
    /// once defaulted differently, and the lower default reproduced exactly that misdiagnosis.
    /// </remarks>
    public static readonly TimeSpan PersistentSetupTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The Cosmos DB emulator's well-known account key.
    /// </summary>
    public const string CosmosDbEmulatorAccountKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
    /// <summary>
    /// The Service Bus emulator image started for declared Service Bus entities.
    /// </summary>
    /// <remarks>
    /// This follows <c>latest</c>, which is what Microsoft's own compose templates and the
    /// Testcontainers module both use; the registry publishes no semantic version tag to pin to. A pull
    /// can therefore change the emulator between runs with nothing in the repository to show for it.
    /// Pin it with <see cref="DockerAzureInfrastructureDefinition.ServiceBusImage"/>, by tag or by
    /// digest, when a run has to be reproducible over time.
    /// </remarks>
    public const string ServiceBusImage = "mcr.microsoft.com/azure-messaging/servicebus-emulator:latest";
}

internal sealed record FunctionAppDefinitionDescriptor(
    Type DefinitionType,
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
    string? TertiarySettingName = null,
    DockerServiceBusEndpoint? ServiceBusEndpoint = null);

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

internal sealed record DockerAzureDefinitionMetadata(
    DockerAzureDefinition Definition,
    Type DefinitionType,
    string RealizedIdentity,
    IReadOnlyCollection<ComponentDependency> Dependencies,
    IReadOnlyCollection<IEnvironmentResourceContract> Provides,
    IReadOnlyCollection<IEnvironmentResourceContract> Requires);
