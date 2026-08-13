using System;
using DotNet.Testcontainers.Containers;
using Microsoft.Data.SqlClient;

namespace TestFramework.Container;

/// <summary>
/// Builds the two addresses every containerized resource has.
/// </summary>
/// <remarks>
/// A container is reachable two different ways, and mixing them up is the most common defect in a
/// container-backed test setup. The test process must use the mapped port on the Docker host; another
/// container on the same network must use the network alias and the internal port. Both are needed
/// for the same resource, at the same time.
/// </remarks>
public static class ContainerEndpoints
{
    /// <summary>
    /// Builds the address the test process uses: the Docker host and the mapped public port.
    /// </summary>
    /// <param name="container">The running container.</param>
    /// <param name="internalPort">The port the container listens on.</param>
    /// <param name="scheme">The URI scheme.</param>
    /// <param name="path">An optional path appended to the address.</param>
    public static Uri HostEndpoint(IContainer container, int internalPort, string scheme = "http", string? path = null)
    {
        ArgumentNullException.ThrowIfNull(container);
        return BuildEndpoint(container.Hostname, container.GetMappedPublicPort(internalPort), scheme, path);
    }

    /// <summary>
    /// Builds the address another container uses: the network alias and the internal port.
    /// </summary>
    /// <param name="networkAlias">The alias the target container was given on the shared network.</param>
    /// <param name="internalPort">The port the container listens on.</param>
    /// <param name="scheme">The URI scheme.</param>
    /// <param name="path">An optional path appended to the address.</param>
    public static Uri NetworkEndpoint(string networkAlias, int internalPort, string scheme = "http", string? path = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(networkAlias);
        return BuildEndpoint(networkAlias, internalPort, scheme, path);
    }

    /// <summary>
    /// Builds a SQL Server connection string for the test process.
    /// </summary>
    /// <param name="container">The running SQL Server container.</param>
    /// <param name="database">The initial catalog.</param>
    /// <param name="userName">The SQL login.</param>
    /// <param name="password">The SQL password.</param>
    /// <param name="internalPort">The port SQL Server listens on inside the container.</param>
    public static string HostSqlConnectionString(IContainer container, string database, string userName, string password, int internalPort = 1433)
    {
        ArgumentNullException.ThrowIfNull(container);
        return BuildSqlConnectionString($"{container.Hostname},{container.GetMappedPublicPort(internalPort)}", database, userName, password);
    }

    /// <summary>
    /// Builds a SQL Server connection string for another container on the same network.
    /// </summary>
    /// <param name="networkAlias">The alias the SQL Server container was given.</param>
    /// <param name="database">The initial catalog.</param>
    /// <param name="userName">The SQL login.</param>
    /// <param name="password">The SQL password.</param>
    /// <param name="internalPort">The port SQL Server listens on inside the container.</param>
    public static string NetworkSqlConnectionString(string networkAlias, string database, string userName, string password, int internalPort = 1433)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(networkAlias);
        return BuildSqlConnectionString($"{networkAlias},{internalPort}", database, userName, password);
    }

    /// <summary>
    /// Builds an absolute address from its parts.
    /// </summary>
    /// <param name="host">The host name.</param>
    /// <param name="port">The port.</param>
    /// <param name="scheme">The URI scheme.</param>
    /// <param name="path">An optional path.</param>
    public static Uri BuildEndpoint(string host, int port, string scheme, string? path = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);

        UriBuilder builder = new()
        {
            Scheme = scheme,
            Host = host,
            Port = port,
            Path = path ?? string.Empty,
        };

        return builder.Uri;
    }

    private static string BuildSqlConnectionString(string dataSource, string database, string userName, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);

        SqlConnectionStringBuilder builder = new()
        {
            DataSource = dataSource,
            InitialCatalog = database,
            UserID = userName,
            Password = password,
            TrustServerCertificate = true,
        };

        return builder.ConnectionString;
    }
}
