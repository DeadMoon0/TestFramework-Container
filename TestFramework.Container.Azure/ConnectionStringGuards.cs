using Microsoft.Data.SqlClient;
using System;
using System.Data.Common;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Azure;

internal static class ConnectionStringGuards
{
    internal static void EnsureAzurite(string connectionString)
    {
        EnsureContainsLocalHost(connectionString, "Azurite");
        if (!connectionString.Contains("devstoreaccount1", StringComparison.OrdinalIgnoreCase))
            throw new FrameworkConfigurationException("Azurite connection string must target the emulator account.");
    }

    internal static void EnsureServiceBus(string connectionString)
    {
        EnsureContainsLocalHost(connectionString, "Service Bus emulator");
        if (!connectionString.Contains("UseDevelopmentEmulator=true", StringComparison.OrdinalIgnoreCase))
            throw new FrameworkConfigurationException("Service Bus emulator connection string must contain UseDevelopmentEmulator=true.");
    }

    internal static void EnsureCosmos(string connectionString)
    {
        EnsureContainsLocalHost(connectionString, "Cosmos emulator");
    }

    internal static void EnsureSql(string connectionString)
    {
        DbConnectionStringBuilder builder = new SqlConnectionStringBuilder(connectionString);
        string dataSource = builder["Data Source"]?.ToString() ?? builder["Server"]?.ToString() ?? string.Empty;

        // The address alone is echoed, never the string: a SQL connection string carries a password.
        if (!IsLocalEndpoint(dataSource))
            throw new FrameworkConfigurationException(
                $"The SQL connection string must target a local Docker emulator endpoint, but it points at '{dataSource}'.",
                [RemoteDaemonHint]);
    }

    private static void EnsureContainsLocalHost(string connectionString, string name)
    {
        if (!IsLocalEndpoint(connectionString))
            throw new FrameworkConfigurationException($"The {name} connection string must target a local Docker emulator endpoint.", [RemoteDaemonHint]);
    }

    /// <summary>
    /// The way out this guard can honestly name.
    /// </summary>
    /// <remarks>
    /// The guard exists so a reset can never purge a real Azure resource, and it draws the line at
    /// loopback. A remote Docker daemon (TESTFRAMEWORK_CONTAINER_HOST_IP, a remote DOCKER_HOST) puts
    /// every emulator behind a non-local address, so this environment cannot tell it from the real
    /// thing and refuses. Saying so beats a bare refusal that names no cause.
    /// </remarks>
    private const string RemoteDaemonHint = "The Docker Azure environment requires a local Docker daemon: against a remote daemon the emulators answer on a remote address, which this guard cannot tell apart from a real Azure resource.";

    private static bool IsLocalEndpoint(string value)
    {
        return value.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || value.Contains("localhost", StringComparison.OrdinalIgnoreCase);
    }
}