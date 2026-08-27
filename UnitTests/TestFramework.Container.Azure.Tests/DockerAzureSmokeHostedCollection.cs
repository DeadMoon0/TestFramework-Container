using TestFramework.Azure;
using TestFramework.Azure.DB.SqlServer;
using TestFramework.Azure.Extensions;
using TestFramework.Container.Azure;
using TestFramework.Config;
using TestFramework.Core.Environment;
using Xunit;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TestFramework.Container.Azure.Tests;

[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class DockerAzureSmokeCollectionDefinition : ICollectionFixture<DockerAzureSmokeCollectionFixture>
{
    public const string CollectionName = "DockerAzureHosted.Smoke";
}

// The base class deliberately does not implement IAsyncLifetime, so that the package does not carry a
// runtime dependency on xunit. Its InitializeAsync/DisposeAsync satisfy the interface implicitly.
public sealed class DockerAzureSmokeCollectionFixture : DockerAzureHostedCollectionFixture<DockerAzureSmokeState>, IAsyncLifetime;

public sealed class DockerAzureSmokeState : IDockerAzureHostedFixtureState
{
    public IReadOnlyList<EnvironmentRequirement> PersistentRequirements =>
    [
        new(AzureEnvironmentResourceKinds.Storage, "storage"),
        new(AzureEnvironmentResourceKinds.Cosmos, "cosmos"),
        new(AzureEnvironmentResourceKinds.Sql, "sql"),
        new(AzureEnvironmentResourceKinds.ServiceBus, "bus"),
        new(AzureEnvironmentResourceKinds.ServiceBus, "func-trigger-bus"),
        new(AzureEnvironmentResourceKinds.ServiceBus, "func-reply-bus"),
        new(AzureEnvironmentResourceKinds.FunctionApp, "func"),
        new(AzureEnvironmentResourceKinds.FunctionApp, "func-sb"),
    ];

    public DockerAzureEnvironment CreateEnvironment()
    {
        return new DockerAzureEnvironment()
            .Include<DockerAzureEnvironmentSmokeTests.SmokeStorageDefinition>()
            .Include<DockerAzureEnvironmentSmokeTests.SmokeCosmosDefinition>()
            .Include<DockerAzureEnvironmentSmokeTests.SmokeServiceBusDefinition>()
            .Include<DockerAzureEnvironmentSmokeTests.SmokeFunctionTriggerBusDefinition>()
            .Include<DockerAzureEnvironmentSmokeTests.SmokeFunctionReplyBusDefinition>()
            .Include<DockerAzureEnvironmentSmokeTests.SmokeFunctionAppDefinition>()
            .Include<DockerAzureEnvironmentSmokeTests.SmokeServiceBusFunctionAppDefinition>();
    }

    public ConfigInstance CreatePersistentConfig()
        => DockerAzureSmokeConfigFactory.CreatePersistentConfig();
}

/// <summary>
/// The persistent slice's configuration: the resources this collection declares, and how its SQL context is built.
/// </summary>
/// <remarks>
/// <para>
/// Declared as configuration rather than by registering a store, because that is what these entries are - things
/// a person wrote down before anything started. They reach a run as its resource values, over which the container
/// stack publishes the addresses it chose, and a step never learns which of the two answered.
/// </para>
/// <para>
/// The addresses here are placeholders on purpose. Every one of them is overwritten when the persistent slice
/// starts and the containers report where they actually ended up; what these entries carry that a container
/// cannot is the names - which blob, which table, which database, which topic.
/// </para>
/// </remarks>
internal static class DockerAzureSmokeConfigFactory
{
    private const string PlaceholderServiceBusConnectionString = "Endpoint=sb://localhost/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=local";

    public static ConfigInstance CreatePersistentConfig()
        => ConfigInstance.Create()
            .LoadDockerAzureConfig()
            .OverrideConfig(DeclaredResources())
            .AddService(services =>
            {
                // No AddDbContext, and no reading of a configuration store: the options handed to this callback
                // already point at the database this run is using, whether a person wrote its address down or
                // the container published one while starting.
                services.AddSqlArtifactContexts(registry => registry.AddForIdentifier<DockerAzureEnvironmentSmokeTests.SmokeSqlDbContext>(
                    "sql",
                    options => new DockerAzureEnvironmentSmokeTests.SmokeSqlDbContext(options)));
            })
            .Build();

    private static Dictionary<string, string?> DeclaredResources()
    {
        Dictionary<string, string?> declared = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["StorageAccount:storage:ConnectionString"] = "UseDevelopmentStorage=true",
            ["StorageAccount:storage:BlobContainerName"] = "smoke-blob",
            ["StorageAccount:storage:TableContainerName"] = DockerAzureEnvironmentSmokeTests.SmokeTableName,

            ["CosmosDb:cosmos:ConnectionString"] = "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;",
            ["CosmosDb:cosmos:DatabaseName"] = "smoke-db",
            ["CosmosDb:cosmos:ContainerName"] = "smoke-container",

            ["SqlDatabase:sql:ConnectionString"] = "Server=localhost;Database=master;User Id=sa;Password=Your_password123;TrustServerCertificate=True",
            ["SqlDatabase:sql:DatabaseName"] = "master",
        };

        AddServiceBus(declared, "bus", queueName: "default-queue", topicName: null, subscriptionName: null);
        AddServiceBus(declared, "func-trigger-bus", queueName: null, topicName: "smoke-trigger-topic", subscriptionName: "smoke-trigger-subscription");
        AddServiceBus(declared, "func-reply-bus", queueName: null, topicName: "smoke-reply-topic", subscriptionName: "smoke-reply-default");

        AddFunctionApp(declared, "func");
        AddFunctionApp(declared, "func-sb");

        return declared;
    }

    private static void AddServiceBus(Dictionary<string, string?> declared, string identifier, string? queueName, string? topicName, string? subscriptionName)
    {
        declared[$"ServiceBus:{identifier}:ConnectionString"] = PlaceholderServiceBusConnectionString;
        declared[$"ServiceBus:{identifier}:QueueName"] = queueName;
        declared[$"ServiceBus:{identifier}:TopicName"] = topicName;
        declared[$"ServiceBus:{identifier}:SubscriptionName"] = subscriptionName;
        declared[$"ServiceBus:{identifier}:RequiredSession"] = bool.FalseString;
    }

    private static void AddFunctionApp(Dictionary<string, string?> declared, string identifier)
    {
        declared[$"FunctionApp:{identifier}:BaseUrl"] = "http://localhost/";
        declared[$"FunctionApp:{identifier}:Code"] = "local-test-key";
    }
}
