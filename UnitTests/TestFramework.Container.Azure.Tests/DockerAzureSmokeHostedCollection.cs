using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TestFramework.Azure;
using TestFramework.Azure.Configuration;
using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Azure.DB.SqlServer;
using TestFramework.Azure.Extensions;
using TestFramework.Container.Azure;
using TestFramework.Config;
using TestFramework.Core.Environment;
using Xunit;

namespace TestFramework.Container.Azure.Tests;

[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class DockerAzureSmokeCollectionDefinition : ICollectionFixture<DockerAzureSmokeCollectionFixture>
{
    public const string CollectionName = "DockerAzureHosted.Smoke";
}

public sealed class DockerAzureSmokeCollectionFixture : DockerAzureHostedCollectionFixture<DockerAzureSmokeState>;

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

internal static class DockerAzureSmokeConfigFactory
{
    public static ConfigInstance CreatePersistentConfig()
        => ConfigInstance.Create()
            .LoadDockerAzureConfig()
            .AddService(services =>
            {
                services.AddSingleton(ConfigStore<StorageAccountConfig>.Create("storage", new StorageAccountConfig
                {
                    ConnectionString = "UseDevelopmentStorage=true",
                    QueueContainerName = null,
                    BlobContainerName = "smoke-blob",
                    TableContainerName = DockerAzureEnvironmentSmokeTests.SmokeTableName,
                }));

                services.AddSingleton(ConfigStore<CosmosContainerDbConfig>.Create("cosmos", new CosmosContainerDbConfig
                {
                    ConnectionString = "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;",
                    DatabaseName = "smoke-db",
                    ContainerName = "smoke-container",
                }));

                services.AddSingleton(ConfigStore<SqlDatabaseConfig>.Create("sql", new SqlDatabaseConfig
                {
                    ConnectionString = "Server=localhost;Database=master;User Id=sa;Password=Your_password123;TrustServerCertificate=True",
                    DatabaseName = "master",
                }));

                ConfigStore<ServiceBusConfig> serviceBusStore = ConfigStore<ServiceBusConfig>.Create("bus", CreateServiceBusConfig("default-queue", null, null));
                serviceBusStore.AddConfig("func-trigger-bus", CreateServiceBusConfig(null, "smoke-trigger-topic", "smoke-trigger-subscription"));
                serviceBusStore.AddConfig("func-reply-bus", CreateServiceBusConfig(null, "smoke-reply-topic", "smoke-reply-default"));
                services.AddSingleton(serviceBusStore);

                ConfigStore<FunctionAppConfig> functionAppStore = ConfigStore<FunctionAppConfig>.Create("func-sb", CreateFunctionAppConfig());
                functionAppStore.AddConfig("func", CreateFunctionAppConfig());
                services.AddSingleton(functionAppStore);

                services.AddDbContext<DockerAzureEnvironmentSmokeTests.SmokeSqlDbContext>((serviceProvider, options) =>
                {
                    SqlDatabaseConfig config = serviceProvider.GetRequiredService<ConfigStore<SqlDatabaseConfig>>().GetConfig("sql");
                    options.UseSqlServer(config.ConnectionString);
                });
                services.AddSqlArtifactContexts(registry => registry.AddForIdentifier<DockerAzureEnvironmentSmokeTests.SmokeSqlDbContext>("sql"));
            })
            .Build();

    private static FunctionAppConfig CreateFunctionAppConfig()
    {
        return new FunctionAppConfig
        {
            BaseUrl = "http://localhost/",
            Code = "local-test-key",
        };
    }

    private static ServiceBusConfig CreateServiceBusConfig(string? queueName, string? topicName, string? subscriptionName)
    {
        return new ServiceBusConfig
        {
            ConnectionString = "Endpoint=sb://localhost/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=local",
            QueueName = queueName,
            TopicName = topicName,
            SubscriptionName = subscriptionName,
            RequiredSession = false,
        };
    }
}
