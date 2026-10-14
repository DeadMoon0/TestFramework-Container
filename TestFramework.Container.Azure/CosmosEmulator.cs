using System;
using System.Net.Http;
using Microsoft.Azure.Cosmos;

namespace TestFramework.Container.Azure;

/// <summary>
/// How a client talks to the Cosmos emulator this package starts, in one place.
/// </summary>
/// <remarks>
/// The same options were written out three times - for the client steps use, the readiness probe and the purge -
/// and one of the three once lacked <see cref="CosmosClientOptions.LimitToEndpoint"/>, which is the one setting
/// that decides whether a call takes a second or half an hour.
/// </remarks>
internal static class CosmosEmulator
{
    /// <summary>
    /// Fresh client options for the emulator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gateway mode, the emulator's development certificate accepted, and the client pinned to the endpoint it was
    /// handed. The emulator advertises its own endpoint, which on a user-defined Docker network is its network alias -
    /// a name only other containers can resolve. Left to discover endpoints, the SDK switches to that one and every
    /// data-plane call retries until it gives up: measured at six minutes on one run and thirty-seven on another.
    /// </para>
    /// <para>
    /// A new instance per call, because the handler factory belongs to one client.
    /// </para>
    /// </remarks>
    public static CosmosClientOptions ClientOptions() => new()
    {
        ConnectionMode = ConnectionMode.Gateway,
        LimitToEndpoint = true,
        HttpClientFactory = static () => new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        }),
    };

    /// <summary>
    /// Reduces a Cosmos connection string to its account endpoint, so a message can name the address without the
    /// key beside it.
    /// </summary>
    public static string DescribeEndpoint(string connectionString)
    {
        foreach (string part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("AccountEndpoint=", StringComparison.OrdinalIgnoreCase))
                return part["AccountEndpoint=".Length..];
        }

        return "(unknown endpoint)";
    }
}
