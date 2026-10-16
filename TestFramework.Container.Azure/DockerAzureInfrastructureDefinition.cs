using System;

namespace TestFramework.Container.Azure;

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
