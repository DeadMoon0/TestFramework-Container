using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Azure.Identifier;

namespace TestFramework.Container.Azure;

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
