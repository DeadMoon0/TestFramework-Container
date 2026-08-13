using System;
using System.Collections.Generic;
using Testcontainers.MsSql;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Web;

/// <summary>
/// The two addresses one provisioned database has.
/// </summary>
/// <param name="HostConnectionString">The address the test process uses: the Docker host and the mapped port.</param>
/// <param name="NetworkConnectionString">The address another container uses: the network alias and the internal port.</param>
/// <remarks>
/// Both exist at the same time for the same database. Handing a container the host address, or the
/// test process the network address, is the most common way a container-backed setup fails.
/// </remarks>
public sealed record SqlDatabaseEndpoint(string HostConnectionString, string NetworkConnectionString);

/// <summary>
/// The running SQL Server and the databases it was provisioned with.
/// </summary>
public sealed class SqlServerComponentState
{
    internal SqlServerComponentState(MsSqlContainer container, IReadOnlyDictionary<string, SqlDatabaseEndpoint> databases)
    {
        Container = container;
        Databases = databases;
    }

    /// <summary>
    /// The running container.
    /// </summary>
    public MsSqlContainer Container { get; }

    /// <summary>
    /// The provisioned databases, by SQL identifier.
    /// </summary>
    public IReadOnlyDictionary<string, SqlDatabaseEndpoint> Databases { get; }

    /// <summary>
    /// Returns the addresses of a provisioned database.
    /// </summary>
    /// <param name="identifier">The SQL identifier.</param>
    /// <exception cref="FrameworkStateException">The identifier was not provisioned.</exception>
    public SqlDatabaseEndpoint GetRequiredDatabase(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);

        if (Databases.TryGetValue(identifier, out SqlDatabaseEndpoint? endpoint))
            return endpoint;

        throw new FrameworkStateException($"No database was provisioned for the SQL identifier '{identifier}'. Provisioned: {(Databases.Count == 0 ? "none" : string.Join(", ", Databases.Keys))}.");
    }
}
