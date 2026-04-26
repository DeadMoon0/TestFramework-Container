using Microsoft.Data.SqlClient;
using System.Data.Common;

namespace TestFramework.Container.Azure;

internal static class ConnectionStringGuards
{
    internal static void EnsureAzurite(string connectionString)
    {
        EnsureContainsLocalHost(connectionString, "Azurite");
        if (!connectionString.Contains("devstoreaccount1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Azurite connection string must target the emulator account.");
    }

    internal static void EnsureServiceBus(string connectionString)
    {
        EnsureContainsLocalHost(connectionString, "Service Bus emulator");
        if (!connectionString.Contains("UseDevelopmentEmulator=true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Service Bus emulator connection string must contain UseDevelopmentEmulator=true.");
    }

    internal static void EnsureCosmos(string connectionString)
    {
        EnsureContainsLocalHost(connectionString, "Cosmos emulator");
    }

    internal static void EnsureSql(string connectionString)
    {
        DbConnectionStringBuilder builder = new SqlConnectionStringBuilder(connectionString);
        string dataSource = builder["Data Source"]?.ToString() ?? builder["Server"]?.ToString() ?? string.Empty;
        if (!IsLocalEndpoint(dataSource))
            throw new InvalidOperationException("SQL connection string must target a local Docker emulator endpoint.");
    }

    private static void EnsureContainsLocalHost(string connectionString, string name)
    {
        if (!IsLocalEndpoint(connectionString))
            throw new InvalidOperationException($"{name} connection string must target a local Docker emulator endpoint.");
    }

    private static bool IsLocalEndpoint(string value)
    {
        return value.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || value.Contains("localhost", StringComparison.OrdinalIgnoreCase);
    }
}