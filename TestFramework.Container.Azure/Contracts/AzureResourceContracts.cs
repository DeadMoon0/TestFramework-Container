using TestFramework.Azure.Identifier;
using TestFramework.Core.Environment;

namespace TestFramework.Container.Azure.Contracts;

/// <summary>
/// Required access mode for blob-container reuse.
/// </summary>
public enum BlobAccessMode
{
    /// <summary>
    /// Read-only blob access is required or provided.
    /// </summary>
    Read,

    /// <summary>
    /// Write-only blob access is required or provided.
    /// </summary>
    Write,

    /// <summary>
    /// Both read and write blob access are required or provided.
    /// </summary>
    ReadWrite
}

/// <summary>
/// Supported Service Bus endpoint kinds for contract matching.
/// </summary>
public enum ServiceBusEndpointKind
{
    /// <summary>
    /// A queue endpoint.
    /// </summary>
    Queue,

    /// <summary>
    /// A topic endpoint.
    /// </summary>
    Topic,

    /// <summary>
    /// A topic subscription endpoint.
    /// </summary>
    TopicSubscription
}

/// <summary>
/// Contract for a blob container exposed through a configured storage account.
/// </summary>
public sealed record BlobContainerContract : IEnvironmentResourceContract
{
    /// <summary>
    /// ContractKey
    /// </summary>
    public required string ContractKey { get; init; }

    /// <summary>
    /// StorageIdentifier
    /// </summary>
    public required StorageAccountIdentifier StorageIdentifier { get; init; }

    /// <summary>
    /// ContainerName
    /// </summary>
    public required string ContainerName { get; init; }

    /// <summary>
    /// BindingName
    /// </summary>
    public string? BindingName { get; init; }

    /// <summary>
    /// AccessMode
    /// </summary>
    public BlobAccessMode AccessMode { get; init; } = BlobAccessMode.ReadWrite;
}

/// <summary>
/// Contract for a Cosmos database/container pair.
/// </summary>
public sealed record CosmosContract : IEnvironmentResourceContract
{
    /// <summary>
    /// ContractKey
    /// </summary>
    public required string ContractKey { get; init; }

    /// <summary>
    /// CosmosIdentifier
    /// </summary>
    public required CosmosContainerIdentifier CosmosIdentifier { get; init; }

    /// <summary>
    /// DatabaseName
    /// </summary>
    public required string DatabaseName { get; init; }

    /// <summary>
    /// ContainerName
    /// </summary>
    public required string ContainerName { get; init; }

    /// <summary>
    /// PartitionKeyPath
    /// </summary>
    public string? PartitionKeyPath { get; init; }

    /// <summary>
    /// BindingName
    /// </summary>
    public string? BindingName { get; init; }
}

/// <summary>
/// Contract for a logical SQL database.
/// </summary>
public sealed record SqlDatabaseContract : IEnvironmentResourceContract
{
    /// <summary>
    /// ContractKey
    /// </summary>
    public required string ContractKey { get; init; }

    /// <summary>
    /// SqlIdentifier
    /// </summary>
    public required SqlDatabaseIdentifier SqlIdentifier { get; init; }

    /// <summary>
    /// DatabaseName
    /// </summary>
    public required string DatabaseName { get; init; }

    /// <summary>
    /// SchemaName
    /// </summary>
    public string? SchemaName { get; init; }

    /// <summary>
    /// BindingName
    /// </summary>
    public string? BindingName { get; init; }
}

/// <summary>
/// Contract for a logical Service Bus endpoint.
/// </summary>
public sealed record ServiceBusEndpointContract : IEnvironmentResourceContract
{
    /// <summary>
    /// ContractKey
    /// </summary>
    public required string ContractKey { get; init; }

    /// <summary>
    /// ServiceBusIdentifier
    /// </summary>
    public required ServiceBusIdentifier ServiceBusIdentifier { get; init; }

    /// <summary>
    /// EndpointKind
    /// </summary>
    public required ServiceBusEndpointKind EndpointKind { get; init; }

    /// <summary>
    /// EntityName
    /// </summary>
    public required string EntityName { get; init; }

    /// <summary>
    /// SubscriptionName
    /// </summary>
    public string? SubscriptionName { get; init; }

    /// <summary>
    /// BindingName
    /// </summary>
    public string? BindingName { get; init; }
}
