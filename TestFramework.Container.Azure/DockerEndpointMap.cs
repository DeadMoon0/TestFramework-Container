using System.Data.Common;
using DotNet.Testcontainers.Containers;

namespace TestFramework.Container.Azure;

internal sealed class DockerEndpointMap
{
    internal string GetFunctionAppBaseUrl(IContainer container)
    {
        return BuildHostEndpoint(container, 80, "http").ToString();
    }

    internal string CreateAzuriteConnectionString(IContainer container)
    {
        return $"DefaultEndpointsProtocol=http;AccountName={DockerAzureDefaults.AzuriteAccountName};AccountKey={DockerAzureDefaults.AzuriteAccountKey};BlobEndpoint={BuildHostEndpoint(container, 10000, "http", "/devstoreaccount1")};QueueEndpoint={BuildHostEndpoint(container, 10001, "http", "/devstoreaccount1")};TableEndpoint={BuildHostEndpoint(container, 10002, "http", "/devstoreaccount1")};";
    }

    internal string CreateCosmosConnectionString(IContainer container)
    {
        return $"AccountEndpoint={BuildHostEndpoint(container, 8081, "https")};AccountKey={DockerAzureDefaults.CosmosDbEmulatorAccountKey};";
    }

    internal string RewriteStorageForContainer(string connectionString)
    {
        DbConnectionStringBuilder builder = CreateBuilder(connectionString);
        builder["BlobEndpoint"] = BuildAliasEndpoint(DockerAzureEnvironment.AzuriteNetworkAlias, 10000, "http", "/devstoreaccount1").ToString();
        builder["QueueEndpoint"] = BuildAliasEndpoint(DockerAzureEnvironment.AzuriteNetworkAlias, 10001, "http", "/devstoreaccount1").ToString();
        builder["TableEndpoint"] = BuildAliasEndpoint(DockerAzureEnvironment.AzuriteNetworkAlias, 10002, "http", "/devstoreaccount1").ToString();
        return SerializeConnectionString(builder);
    }

    internal string RewriteCosmosForContainer(string connectionString)
    {
        DbConnectionStringBuilder builder = CreateBuilder(connectionString);
        builder["AccountEndpoint"] = BuildAliasEndpoint(DockerAzureEnvironment.CosmosDbNetworkAlias, 8081, "https").ToString();
        return SerializeConnectionString(builder);
    }

    internal string RewriteServiceBusForContainer(string connectionString)
    {
        DbConnectionStringBuilder builder = CreateBuilder(connectionString);
        builder["Endpoint"] = $"sb://{DockerAzureEnvironment.ServiceBusNetworkAlias}/";
        return SerializeConnectionString(builder);
    }

    private static DbConnectionStringBuilder CreateBuilder(string connectionString)
    {
        return new DbConnectionStringBuilder
        {
            ConnectionString = connectionString,
        };
    }

    private static Uri BuildHostEndpoint(IContainer container, int internalPort, string scheme, string? path = null)
    {
        return BuildEndpoint(container.Hostname, container.GetMappedPublicPort(internalPort), scheme, path);
    }

    private static Uri BuildAliasEndpoint(string host, int port, string scheme, string? path = null)
    {
        return BuildEndpoint(host, port, scheme, path);
    }

    private static Uri BuildEndpoint(string host, int port, string scheme, string? path)
    {
        UriBuilder builder = new()
        {
            Scheme = scheme,
            Host = host,
            Port = port,
            Path = path ?? string.Empty,
        };

        return builder.Uri;
    }

    private static string SerializeConnectionString(DbConnectionStringBuilder builder)
    {
        List<string> parts = [];

        foreach (string key in builder.Keys.Cast<string>())
            parts.Add($"{key}={builder[key]}");

        return string.Join(';', parts) + ";";
    }
}