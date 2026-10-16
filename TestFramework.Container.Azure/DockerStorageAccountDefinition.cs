using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Azure.Identifier;

namespace TestFramework.Container.Azure;

/// <summary>
/// A storage account served by the Azurite container.
/// </summary>
/// <remarks>
/// When at least one container name is set, the definition declares a storage account configuration for
/// <see cref="Identifier"/> to the run; its connection string is replaced by the address Azurite publishes.
/// </remarks>
public abstract class DockerStorageAccountDefinition : DockerAzureDefinition
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
    protected virtual string? QueueName => null;

    /// <summary>
    /// The table name declared in the storage account configuration, or <c>null</c> for none.
    /// </summary>
    protected virtual string? TableName => null;

    internal StorageAccountConfig BuildConfig(string connectionString) => new()
    {
        ConnectionString = connectionString,
        BlobContainerName = BlobContainerName,
        QueueName = QueueName,
        TableName = TableName,
    };

    internal bool TryCreateDefaultConfig(out StorageAccountConfig config)
    {
        if (BlobContainerName is null && QueueName is null && TableName is null)
        {
            config = default!;
            return false;
        }

        config = BuildConfig(DockerAzureDefaults.PlaceholderConnectionString);
        return true;
    }
}
