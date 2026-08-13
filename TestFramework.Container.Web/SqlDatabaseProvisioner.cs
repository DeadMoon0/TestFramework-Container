using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using TestFramework.Core.Logging;
using TestFramework.Web.Sql.Model;
using TestFramework.Web.Sql.Schema;
using TestFramework.Web.Sql.Steps;

namespace TestFramework.Container.Web;

/// <summary>
/// Brings a declared database into the state a run expects.
/// </summary>
/// <remarks>
/// The order is fixed and matters: the database exists, then it has a schema, then it is reset. A
/// reset script that runs before the schema would fail on the first run, when there is nothing yet
/// to clear.
/// </remarks>
public static class SqlDatabaseProvisioner
{
    /// <summary>
    /// Creates the database, dropping it first when the declaration asks for that.
    /// </summary>
    /// <param name="serverConnectionString">A connection string reaching the server, not the database.</param>
    /// <param name="spec">The declaration to apply.</param>
    /// <param name="logger">The scoped logger.</param>
    /// <param name="cancellationToken">The cancellation token for the running setup.</param>
    public static async Task EnsureDatabaseAsync(string serverConnectionString, DockerSqlSpec spec, ScopedLogger logger, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverConnectionString);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(logger);

        string quoted = SqlModelMap.Quote(spec.DatabaseName);

        if (spec.ResetMode == SqlResetMode.RecreateDatabase)
        {
            logger.LogInformation("Recreating the database '{0}'.", spec.DatabaseName);

            // Sessions left open by a previous run would block the drop, so they are rolled back.
            await ExecuteAsync(
                serverConnectionString,
                $"""
                IF DB_ID(N'{spec.DatabaseName}') IS NOT NULL
                BEGIN
                    ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE {quoted};
                END
                """,
                cancellationToken).ConfigureAwait(false);
        }

        // CREATE DATABASE cannot share a batch with other statements, so it is wrapped in EXEC.
        await ExecuteAsync(
            serverConnectionString,
            $"IF DB_ID(N'{spec.DatabaseName}') IS NULL EXEC(N'CREATE DATABASE {quoted};');",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the generated tables, the declared scripts and the reset script, in that order.
    /// </summary>
    /// <param name="connectionString">A connection string reaching the database itself.</param>
    /// <param name="spec">The declaration to apply.</param>
    /// <param name="registry">The model registry the generated tables are derived from.</param>
    /// <param name="logger">The scoped logger.</param>
    /// <param name="cancellationToken">The cancellation token for the running setup.</param>
    public static async Task ApplySchemaAsync(
        string connectionString,
        DockerSqlSpec spec,
        SqlModelRegistry registry,
        ScopedLogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);

        if (spec.ModelTypes.Count > 0)
        {
            SqlScript generated = SqlSchema.CreateTablesScript(registry, spec.ModelTypes);
            logger.LogInformation("Creating the tables of '{0}' from {1} model(s).", spec.DatabaseName, spec.ModelTypes.Count);
            await RunScriptAsync(connectionString, generated, cancellationToken).ConfigureAwait(false);
        }

        foreach (SqlScript script in spec.Scripts)
        {
            logger.LogInformation("Running '{0}' against '{1}'.", script.Description, spec.DatabaseName);
            await RunScriptAsync(connectionString, script, cancellationToken).ConfigureAwait(false);
        }

        if (spec.ResetMode == SqlResetMode.RunResetScript && spec.ResetScript is { } resetScript)
        {
            logger.LogInformation("Resetting '{0}' with '{1}'.", spec.DatabaseName, resetScript.Description);
            await RunScriptAsync(connectionString, resetScript, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs a script batch by batch.
    /// </summary>
    /// <param name="connectionString">The connection string to run against.</param>
    /// <param name="script">The script to run.</param>
    /// <param name="cancellationToken">The cancellation token for the running setup.</param>
    public static async Task RunScriptAsync(string connectionString, SqlScript script, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(script);

        foreach (string batch in script.SplitBatches())
            await ExecuteAsync(connectionString, batch, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(string connectionString, string statement, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = new(statement, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
