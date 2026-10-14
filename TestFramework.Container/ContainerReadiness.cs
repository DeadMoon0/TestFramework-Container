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
    /// Waits until an HTTP endpoint answers with a status that proves the service is up.
    /// </summary>
    /// <param name="baseAddress">The base address to probe.</param>
    /// <param name="path">The path to request, relative to the base address.</param>
    /// <param name="timeout">How long to keep trying.</param>
    /// <param name="description">A description used in log and error output, such as the identifier.</param>
    /// <param name="logger">The scoped logger.</param>
    /// <param name="cancellationToken">The cancellation token for the running setup.</param>
    /// <exception cref="FrameworkTimeoutException">The endpoint did not answer within the timeout.</exception>
    public static Task WaitForHttpAsync(
        Uri baseAddress,
        string path,
        TimeSpan timeout,
        string description,
        ScopedLogger logger,
        CancellationToken cancellationToken)
        => WaitForHttpAsync(baseAddress, path, timeout, description, logger, AnsweringStatusCodes.Contains, cancellationToken);

    /// <summary>
    /// Waits until an HTTP endpoint answers at all, whatever the status.
    /// </summary>
    /// <param name="baseAddress">The base address to probe.</param>
    /// <param name="path">The path to request, relative to the base address.</param>
    /// <param name="timeout">How long to keep trying.</param>
    /// <param name="description">A description used in log and error output, such as the identifier.</param>
    /// <param name="logger">The scoped logger.</param>
    /// <param name="cancellationToken">The cancellation token for the running setup.</param>
    /// <remarks>
    /// For a service with no health endpoint, where a <c>404</c> still proves something is listening.
    /// </remarks>
    /// <exception cref="FrameworkTimeoutException">The endpoint did not answer within the timeout.</exception>
    public static Task WaitForHttpAnswerAsync(
        Uri baseAddress,
        string path,
        TimeSpan timeout,
        string description,
        ScopedLogger logger,
        CancellationToken cancellationToken)
        => WaitForHttpAsync(baseAddress, path, timeout, description, logger, _ => true, cancellationToken);

    /// <summary>
    /// Waits until an HTTP endpoint answers with a status the caller accepts.
    /// </summary>
    /// <param name="baseAddress">The base address to probe.</param>
    /// <param name="path">The path to request, relative to the base address.</param>
    /// <param name="timeout">How long to keep trying.</param>
    /// <param name="description">A description used in log and error output, such as the identifier.</param>
    /// <param name="logger">The scoped logger.</param>
    /// <param name="isReady">Decides whether a status proves readiness.</param>
    /// <param name="cancellationToken">The cancellation token for the running setup.</param>
    /// <exception cref="FrameworkTimeoutException">The endpoint did not answer within the timeout.</exception>
    public static async Task WaitForHttpAsync(
        Uri baseAddress,
        string path,
        TimeSpan timeout,
        string description,
        ScopedLogger logger,
        Func<HttpStatusCode, bool> isReady,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(isReady);

        using HttpClient client = new() { BaseAddress = baseAddress };
        logger.LogInformation("Waiting up to {0} for '{1}' at '{2}'.", timeout, description, new Uri(baseAddress, path));

        await WaitUntilAsync(
            async token =>
            {
                using HttpResponseMessage response = await client.GetAsync(path, token).ConfigureAwait(false);
                return isReady(response.StatusCode);
            },
            static exception => exception is HttpRequestException,
            timeout,
            (_, _) => new FrameworkTimeoutException(
                $"'{description}' at '{baseAddress}' did not answer within {timeout:g}. "
                + "Check the container log for a startup failure, or raise the readiness timeout for a slow machine."),
            cancellationToken).ConfigureAwait(false);
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

        await WaitUntilAsync(
            async token =>
            {
                await using SqlConnection connection = new(connectionString);
                await connection.OpenAsync(token).ConfigureAwait(false);
                await using SqlCommand command = new(statement, connection);
                await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                return true;
            },
            static exception => exception is SqlException or InvalidOperationException,
            timeout,
            (_, lastFailure) => new FrameworkTimeoutException(
                $"'{description}' did not become usable within {timeout:g}. Last failure: {lastFailure?.Message ?? "(none)"}",
                lastFailure),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Tries until an attempt succeeds, within a window that the step's own deadline can end early.
    /// </summary>
    /// <param name="attempt">One try. Returns whether the service is ready; a transient failure may also throw.</param>
    /// <param name="isTransient">Which exceptions mean "not yet" rather than "broken".</param>
    /// <param name="timeout">How long the service gets to become ready.</param>
    /// <param name="timedOut">The exception to raise when the window closes: given the attempts made and the last
    /// transient failure.</param>
    /// <param name="cancellationToken">The step's deadline, or whatever else ends the wait early.</param>
    /// <remarks>
    /// <para>
    /// One clock and one owner. The waits used to count down on the wall clock beside the step's deadline, so a
    /// readiness timeout longer than the step ran on after the step had already been given up, and a clock change
    /// could stretch or cut it. The window here is a token linked to <paramref name="cancellationToken"/>: it closes
    /// at <paramref name="timeout"/> on a monotonic timer, or earlier when the deadline ends.
    /// </para>
    /// <para>
    /// Every attempt is bounded by the same window even when it ignores the token it is handed - the Cosmos SDK's
    /// account read takes none - so a hanging attempt cannot outlive the wait it belongs to.
    /// </para>
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> ended the wait.</exception>
    public static async Task WaitUntilAsync(
        Func<CancellationToken, Task<bool>> attempt,
        Func<Exception, bool> isTransient,
        TimeSpan timeout,
        Func<int, Exception?, Exception> timedOut,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(isTransient);
        ArgumentNullException.ThrowIfNull(timedOut);

        using CancellationTokenSource window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(timeout);

        int attempts = 0;
        Exception? lastFailure = null;

        try
        {
            while (true)
            {
                attempts++;
                try
                {
                    if (await attempt(window.Token).WaitAsync(window.Token).ConfigureAwait(false))
                        return;
                }
                catch (Exception exception) when (exception is not OperationCanceledException && isTransient(exception))
                {
                    lastFailure = exception;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), window.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw timedOut(attempts, lastFailure);
        }
    }
}
