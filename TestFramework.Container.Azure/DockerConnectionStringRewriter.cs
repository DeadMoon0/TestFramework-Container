using System.Data.Common;

namespace TestFramework.Container.Azure;

internal static class DockerConnectionStringRewriter
{
    internal static string RewriteStorageForContainer(string connectionString)
    {
        DbConnectionStringBuilder builder = CreateBuilder(connectionString);
        builder["BlobEndpoint"] = RewriteEndpoint((string)builder["BlobEndpoint"], DockerAzureEnvironment.AzuriteNetworkAlias, 10000);
        builder["QueueEndpoint"] = RewriteEndpoint((string)builder["QueueEndpoint"], DockerAzureEnvironment.AzuriteNetworkAlias, 10001);
        builder["TableEndpoint"] = RewriteEndpoint((string)builder["TableEndpoint"], DockerAzureEnvironment.AzuriteNetworkAlias, 10002);
        return SerializeConnectionString(builder);
    }

    internal static string RewriteCosmosForContainer(string connectionString)
    {
        DbConnectionStringBuilder builder = CreateBuilder(connectionString);
        builder["AccountEndpoint"] = RewriteEndpoint((string)builder["AccountEndpoint"], DockerAzureEnvironment.CosmosDbNetworkAlias, 8081);
        return SerializeConnectionString(builder);
    }

    internal static string RewriteServiceBusForSameNetworkContainer(string connectionString)
    {
        DbConnectionStringBuilder builder = CreateBuilder(connectionString);
        builder["Endpoint"] = RewriteServiceBusEndpoint(DockerAzureEnvironment.ServiceBusNetworkAlias);
        return SerializeConnectionString(builder);
    }

    private static DbConnectionStringBuilder CreateBuilder(string connectionString)
    {
        DbConnectionStringBuilder builder = new()
        {
            ConnectionString = connectionString,
        };
        return builder;
    }

    private static string RewriteEndpoint(string endpoint, string host, int port)
    {
        UriBuilder uriBuilder = new(endpoint)
        {
            Host = host,
            Port = port,
        };

        return uriBuilder.Uri.ToString();
    }

    private static string RewriteServiceBusEndpoint(string host)
    {
        return $"sb://{host}/";
    }

    private static string SerializeConnectionString(DbConnectionStringBuilder builder)
    {
        List<string> parts = [];

        foreach (string key in builder.Keys.Cast<string>())
            parts.Add($"{key}={builder[key]}");

        return string.Join(';', parts) + ";";
    }
}