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
public sealed record BlobContainerContract(
    string ContractKey,
    StorageAccountIdentifier StorageIdentifier,
    string ContainerName,
    string? BindingName = null,
    BlobAccessMode AccessMode = BlobAccessMode.ReadWrite) : IEnvironmentResourceContract;

/// <summary>
/// Contract for a Cosmos database/container pair.
/// </summary>
public sealed record CosmosContract(
    string ContractKey,
    CosmosContainerIdentifier CosmosIdentifier,
    string DatabaseName,
    string ContainerName,
    string? PartitionKeyPath = null,
    string? BindingName = null) : IEnvironmentResourceContract;

/// <summary>
/// Contract for a logical SQL database.
/// </summary>
public sealed record SqlDatabaseContract(
    string ContractKey,
    SqlDatabaseIdentifier SqlIdentifier,
    string DatabaseName,
    string? SchemaName = null,
    string? BindingName = null) : IEnvironmentResourceContract;

/// <summary>
/// Contract for a logical Service Bus endpoint.
/// </summary>
public sealed record ServiceBusEndpointContract(
    string ContractKey,
    ServiceBusIdentifier ServiceBusIdentifier,
    ServiceBusEndpointKind EndpointKind,
    string EntityName,
    string? SubscriptionName = null,
    string? BindingName = null) : IEnvironmentResourceContract;
