using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container;

/// <summary>
/// Creates and recreates databases on a SQL Server container, under one set of safety rules.
/// </summary>
/// <remarks>
/// <para>
/// Container.Web and Container.Azure each had their own drop-and-recreate against the same image from the same
/// factory, with different rules: one put a validated name straight into the statement, the other parameterised
/// it, quoted it, and skipped a list of protected names. Two copies of a statement that drops databases is one too
/// many places to get it wrong.
/// </para>
/// <para>
/// The list was wrong, too. It protected names starting with "SbEmulator" or containing "ServiceBus"; measured
/// against the emulator image, its state lives in <c>SbGatewayDatabase</c> and <c>SbMessageContainerDatabase00001</c>,
/// neither of which it matched. Dropping either ends the Service Bus emulator mid-suite.
/// </para>
/// </remarks>
public static partial class SqlDatabaseLifecycle
{
    /// <summary>
    /// Whether a database belongs to the server or to the Service Bus emulator, and so is never dropped.
    /// </summary>
    /// <param name="databaseName">The database name.</param>
    public static bool IsProtected(string databaseName)
    {
        ArgumentNullException.ThrowIfNull(databaseName);

        return databaseName.ToLowerInvariant() is "master" or "model" or "msdb" or "tempdb"
            || string.Equals(databaseName, "SbGatewayDatabase", StringComparison.OrdinalIgnoreCase)
            || ServiceBusMessageContainer().IsMatch(databaseName);
    }

    /// <summary>
    /// Creates the database when it does not exist yet.
    /// </summary>
    /// <param name="serverConnectionString">A connection string reaching the server; its catalog is ignored.</param>
    /// <param name="databaseName">The database to create.</param>
    /// <param name="cancellationToken">What stops the statement.</param>
    public static Task EnsureExistsAsync(string serverConnectionString, string databaseName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        // CREATE DATABASE refuses to share a batch, hence the EXEC.
        return ExecuteAsync(
            serverConnectionString,
            $"IF DB_ID(@name) IS NULL EXEC(N'CREATE DATABASE {QuoteName(databaseName).Replace("'", "''", StringComparison.Ordinal)};');",
            databaseName,
            cancellationToken);
    }

    /// <summary>
    /// Drops the database when it exists, rolling back open sessions, and creates it again empty.
    /// </summary>
    /// <param name="serverConnectionString">A connection string reaching the server; its catalog is ignored.</param>
    /// <param name="databaseName">The database to recreate.</param>
    /// <param name="cancellationToken">What stops the statements.</param>
    /// <exception cref="FrameworkConfigurationException">The name is a system database or one the Service Bus emulator
    /// keeps its state in.</exception>
    public static async Task RecreateAsync(string serverConnectionString, string databaseName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        if (IsProtected(databaseName))
        {
            throw new FrameworkConfigurationException($"The database '{databaseName}' is not dropped: it belongs to SQL Server itself or to the Service Bus emulator.")
            {
                RecoverySteps = ["Give the test database a name of its own."],
            };
        }

        // Sessions a previous run left open would block the drop, so they are rolled back.
        await ExecuteAsync(
            serverConnectionString,
            $"""
            IF DB_ID(@name) IS NOT NULL
            BEGIN
                ALTER DATABASE {QuoteName(databaseName)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE {QuoteName(databaseName)};
            END
            """,
            databaseName,
            cancellationToken).ConfigureAwait(false);

        await EnsureExistsAsync(serverConnectionString, databaseName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A name as SQL Server's <c>QUOTENAME</c> would write it, for the places a parameter cannot go.
    /// </summary>
    internal static string QuoteName(string identifier)
        => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static async Task ExecuteAsync(string serverConnectionString, string statement, string databaseName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverConnectionString);

        // A database cannot be dropped from a connection that is inside it.
        SqlConnectionStringBuilder server = new(serverConnectionString) { InitialCatalog = "master" };

        await using SqlConnection connection = new(server.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = new(statement, connection);
        command.Parameters.AddWithValue("@name", databaseName);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [GeneratedRegex(@"^SbMessageContainerDatabase\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ServiceBusMessageContainer();
}
