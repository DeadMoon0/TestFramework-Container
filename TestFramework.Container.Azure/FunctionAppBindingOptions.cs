using TestFramework.Core.Environment;

namespace TestFramework.Container.Azure;

/// <summary>
/// How a storage definition is bound into a Function App container: which app settings carry what.
/// </summary>
/// <remarks>
/// Settings rather than parameters, because this set grows: the table name was once inserted as a new optional
/// parameter in the middle of the list, and every caller compiled against the old list stopped resolving. A new
/// setting here is one more init property, which no compiled caller notices.
/// </remarks>
public sealed record FunctionAppStorageAccountBinding
{
    /// <summary>The defaults, as the Functions templates name them.</summary>
    public static FunctionAppStorageAccountBinding Default { get; } = new();

    /// <summary>The app setting carrying the storage connection string the app reads.</summary>
    public string ConnectionSettingName { get; init; } = "StorageAccountConnectionString";

    /// <summary>The app setting carrying the Functions host's own storage connection string.</summary>
    public string HostStorageSettingName { get; init; } = "AzureWebJobsStorage";

    /// <summary>The app setting carrying the table name, or <see langword="null"/> to inject none.</summary>
    public string? TableNameSettingName { get; init; } = "StorageTableName";

    /// <summary>Whether the storage may be shared with other definitions.</summary>
    public DependencyOwnership Ownership { get; init; } = DependencyOwnership.Shared;
}

/// <summary>
/// How a Cosmos definition is bound into a Function App container.
/// </summary>
public sealed record FunctionAppCosmosDbBinding
{
    /// <summary>The defaults, as the Functions templates name them.</summary>
    public static FunctionAppCosmosDbBinding Default { get; } = new();

    /// <summary>The app setting carrying the Cosmos connection string.</summary>
    public string ConnectionSettingName { get; init; } = "CosmosDbConnectionString";

    /// <summary>The app setting carrying the database name.</summary>
    public string DatabaseSettingName { get; init; } = "CosmosDatabaseName";

    /// <summary>The app setting carrying the container name.</summary>
    public string ContainerSettingName { get; init; } = "CosmosContainerName";

    /// <summary>Whether the Cosmos emulator may be shared with other definitions.</summary>
    public DependencyOwnership Ownership { get; init; } = DependencyOwnership.Shared;
}

/// <summary>
/// How a Service Bus endpoint is bound into a Function App container as the trigger it listens on.
/// </summary>
public sealed record FunctionAppServiceBusTriggerBinding
{
    /// <summary>The defaults, as the Functions templates name them.</summary>
    public static FunctionAppServiceBusTriggerBinding Default { get; } = new();

    /// <summary>The app setting carrying the Service Bus connection string.</summary>
    public string ConnectionSettingName { get; init; } = "ServiceBusTriggerConnection";

    /// <summary>The app setting carrying the queue or topic name.</summary>
    public string EntitySettingName { get; init; } = "ServiceBusTriggerTopicName";

    /// <summary>The app setting carrying the subscription name, or <see langword="null"/> for a queue.</summary>
    public string? SubscriptionSettingName { get; init; } = "ServiceBusTriggerSubscriptionName";

    /// <summary>Whether the Service Bus emulator may be shared with other definitions.</summary>
    public DependencyOwnership Ownership { get; init; } = DependencyOwnership.Shared;
}

/// <summary>
/// How a Service Bus endpoint is bound into a Function App container as the entity it replies to.
/// </summary>
public sealed record FunctionAppServiceBusReplyBinding
{
    /// <summary>The defaults, as the Functions templates name them.</summary>
    public static FunctionAppServiceBusReplyBinding Default { get; } = new();

    /// <summary>The app setting carrying the Service Bus connection string.</summary>
    public string ConnectionSettingName { get; init; } = "ServiceBusReplyConnectionString";

    /// <summary>The app setting carrying the queue or topic name.</summary>
    public string EntitySettingName { get; init; } = "ServiceBusReplyTopicName";

    /// <summary>Whether the Service Bus emulator may be shared with other definitions.</summary>
    public DependencyOwnership Ownership { get; init; } = DependencyOwnership.Shared;
}
