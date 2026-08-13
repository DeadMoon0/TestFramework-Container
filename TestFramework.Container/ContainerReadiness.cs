using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using TestFramework.Core.Exceptions;
using TestFramework.Core.Logging;

namespace TestFramework.Container;

/// <summary>
/// Waits for a started container to actually be usable.
/// </summary>
/// <remarks>
/// A started container is not a ready one. Publishing an endpoint before the service behind it
/// answers turns a startup race into a confusing test failure much later, so components wait here
/// before writing anything into a configuration store.
/// </remarks>
public static class ContainerReadiness
{
    /// <summary>
    /// Status codes that prove an HTTP host is answering, even when the probe itself is rejected.
    /// </summary>
    private static readonly HashSet<HttpStatusCode> AnsweringStatusCodes =
    [
        HttpStatusCode.OK,
        HttpStatusCode.NoContent,
        HttpStatusCode.Unauthorized,
        HttpStatusCode.Forbidden,
    ];

    /// <summary>
    /// Waits until an HTTP endpoint answers.
    /// </summary>
    /// <param name="baseAddress">The base address to probe.</param>
    /// <param name="path">The path to request, relative to the base address.</param>
    /// <param name="timeout">How long to keep trying.</param>
    /// <param name="description">A description used in log and error output, such as the identifier.</param>
    /// <param name="logger">The scoped logger.</param>
    /// <param name="cancellationToken">The cancellation token for the running setup.</param>
    /// <exception cref="FrameworkTimeoutException">The endpoint did not answer within the timeout.</exception>
    public static async Task WaitForHttpAsync(
        Uri baseAddress,
        string path,
        TimeSpan timeout,
        string description,
        ScopedLogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(logger);

        using HttpClient client = new() { BaseAddress = baseAddress };
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        logger.LogInformation("Waiting up to {0} for '{1}' at '{2}'.", timeout, description, new Uri(baseAddress, path));

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using HttpResponseMessage response = await client.GetAsync(path, cancellationToken).ConfigureAwait(false);
                if (AnsweringStatusCodes.Contains(response.StatusCode))
                    return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        throw new FrameworkTimeoutException(
            $"'{description}' at '{baseAddress}' did not answer within {timeout:g}. "
            + "Check the container log for a startup failure, or raise the readiness timeout for a slow machine.");
    }

    /// <summary>
    /// Waits until a SQL Server connection can be opened and a statement executed.
    /// </summary>
    /// <param name="connectionString">The connection string to probe with.</param>
    /// <param name="timeout">How long to keep trying.</param>
    /// <param name="description">A description used in error output, such as the identifier.</param>
    /// <param name="cancellationToken">The cancellation token for the running setup.</param>
    /// <exception cref="FrameworkTimeoutException">The server did not become usable within the timeout.</exception>
    public static Task WaitForSqlAsync(string connectionString, TimeSpan timeout, string description, CancellationToken cancellationToken)
        => WaitForSqlStatementAsync(connectionString, "SELECT 1;", timeout, description, cancellationToken);

    /// <summary>
    /// Waits until a statement succeeds, which is how readiness of a specific database is proven.
    /// </summary>
    /// <param name="connectionString">The connection string to probe with.</param>
    /// <param name="statement">The statement to execute.</param>
    /// <param name="timeout">How long to keep trying.</param>
    /// <param name="description">A description used in error output.</param>
    /// <param name="cancellationToken">The cancellation token for the running setup.</param>
    /// <exception cref="FrameworkTimeoutException">The statement did not succeed within the timeout.</exception>
    public static async Task WaitForSqlStatementAsync(
        string connectionString,
        string statement,
        TimeSpan timeout,
        string description,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(statement);

        DateTime deadline = DateTime.UtcNow.Add(timeout);
        Exception? lastFailure = null;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using SqlConnection connection = new(connectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using SqlCommand command = new(statement, connection);
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is SqlException or InvalidOperationException)
            {
                lastFailure = exception;
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new FrameworkTimeoutException(
            $"'{description}' did not become usable within {timeout:g}. Last failure: {lastFailure?.Message ?? "(none)"}",
            lastFailure);
    }
}
