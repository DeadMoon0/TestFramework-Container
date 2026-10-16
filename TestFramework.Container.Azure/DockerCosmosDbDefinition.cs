using System;
using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Azure.Identifier;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Azure;

/// <summary>
/// A Cosmos DB container served by the Cosmos DB emulator container.
/// </summary>
/// <remarks>
/// When both <see cref="DatabaseName"/> and <see cref="ContainerName"/> are set, the definition declares a Cosmos
/// configuration for <see cref="Identifier"/> to the run; its connection string is replaced by the address the
/// emulator publishes.
/// </remarks>
public abstract class DockerCosmosDbDefinition : DockerAzureDefinition
{
    /// <summary>
    /// The Cosmos container identifier steps and configuration refer to.
    /// </summary>
    public abstract CosmosDbIdentifier Identifier { get; }

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

    internal CosmosDbConfig BuildConfig(string connectionString) => new()
    {
        ConnectionString = connectionString,
        DatabaseName = DatabaseName ?? throw new FrameworkConfigurationException($"Override '{nameof(DatabaseName)}' on '{GetType().Name}' to provide a Cosmos database name."),
        ContainerName = ContainerName ?? throw new FrameworkConfigurationException($"Override '{nameof(ContainerName)}' on '{GetType().Name}' to provide a Cosmos container name."),
    };

    internal bool TryCreateDefaultConfig(out CosmosDbConfig config)
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
/// A <see cref="DockerCosmosDbDefinition"/> whose document type is <typeparamref name="TDocument"/>.
/// </summary>
/// <typeparam name="TDocument">The document type; the container's partition key path is read from it.</typeparam>
public abstract class DockerCosmosDbDefinition<TDocument> : DockerCosmosDbDefinition
{
    /// <summary>
    /// Always <typeparamref name="TDocument"/>.
    /// </summary>
    public sealed override Type ModelType => typeof(TDocument);
}
