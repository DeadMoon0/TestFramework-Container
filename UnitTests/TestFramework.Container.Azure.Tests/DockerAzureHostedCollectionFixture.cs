using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TestFramework.Azure;
using TestFramework.Azure.Configuration;
using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Azure.DB.SqlServer;
using TestFramework.Azure.Extensions;
using TestFramework.Container.Azure;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;
using Xunit;

namespace TestFramework.Container.Azure.Tests;

[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class DockerAzureHostedCollectionDefinition : ICollectionFixture<DockerAzureHostedCollectionFixture>
{
    public const string CollectionName = "DockerAzureHosted";
}

public sealed class DockerAzureHostedCollectionFixture : IAsyncLifetime
{
    private static readonly IReadOnlyList<EnvironmentRequirement> BootstrapRequirements =
    [
        new(AzureEnvironmentResourceKinds.Storage, "storage"),
        new(AzureEnvironmentResourceKinds.Cosmos, "cosmos"),
        new(AzureEnvironmentResourceKinds.Sql, "sql"),
        new(AzureEnvironmentResourceKinds.ServiceBus, "bus"),
        new(AzureEnvironmentResourceKinds.ServiceBus, "func-trigger-bus"),
        new(AzureEnvironmentResourceKinds.ServiceBus, "func-reply-bus"),
        new(AzureEnvironmentResourceKinds.FunctionApp, "func"),
        new(AzureEnvironmentResourceKinds.FunctionApp, "func-sb"),
        new(AzureEnvironmentResourceKinds.LogicApp, "logic"),
    ];

    private readonly DockerAzureEnvironment _bootstrapEnvironment = CreateBootstrapEnvironment();
    private DockerAzureHostedEnvironment? _hostedEnvironment;

    private DockerAzureHostedSnapshot? _snapshot;

    public async Task InitializeAsync()
    {
        ServiceProvider bootstrapServiceProvider = DockerAzureSmokeServiceProviderFactory.CreateBootstrapProvider();
        _hostedEnvironment = await DockerAzureHostedEnvironment.StartAsync(
            _bootstrapEnvironment,
            bootstrapServiceProvider,
            BootstrapRequirements,
            disposeBootstrapServiceProvider: true).ConfigureAwait(false);
        _snapshot = DockerAzureHostedSnapshot.From(bootstrapServiceProvider);
    }

    public async Task DisposeAsync()
    {
        if (_hostedEnvironment is not null)
            await _hostedEnvironment.DisposeAsync().ConfigureAwait(false);
    }

    public ServiceProvider CreateServiceProvider(bool withFunctionApp = false)
    {
        DockerAzureHostedSnapshot snapshot = _snapshot ?? throw new InvalidOperationException("The hosted Docker Azure fixture has not finished initialization.");
        return DockerAzureSmokeServiceProviderFactory.Create(snapshot, withFunctionApp);
    }

    public IEnvironmentProvider CreateEnvironment()
    {
        return (_hostedEnvironment ?? throw new InvalidOperationException("The hosted Docker Azure fixture has not finished initialization.")).CreateEnvironment();
    }

    private static DockerAzureEnvironment CreateBootstrapEnvironment()
    {
        return new DockerAzureEnvironment()
            .Include<DockerAzureEnvironmentSmokeTests.SmokeStorageDefinition>()
            .Include<DockerAzureEnvironmentSmokeTests.SmokeCosmosDefinition>()
            .Include<DockerAzureEnvironmentSmokeTests.SmokeServiceBusDefinition>()
            .Include<DockerAzureEnvironmentSmokeTests.SmokeFunctionTriggerBusDefinition>()
            .Include<DockerAzureEnvironmentSmokeTests.SmokeFunctionReplyBusDefinition>()
            .Include<DockerAzureEnvironmentSmokeTests.SmokeFunctionAppDefinition>()
            .Include<DockerAzureEnvironmentSmokeTests.SmokeLogicAppDefinition>()
            .Include<DockerAzureEnvironmentSmokeTests.SmokeServiceBusFunctionAppDefinition>();
    }
}

internal sealed record DockerAzureHostedSnapshot(
    StorageAccountConfig Storage,
    CosmosContainerDbConfig Cosmos,
    SqlDatabaseConfig Sql,
    IReadOnlyDictionary<string, ServiceBusConfig> ServiceBus,
    IReadOnlyDictionary<string, FunctionAppConfig> FunctionApps,
    IReadOnlyDictionary<string, LogicAppConfig> LogicApps)
{
    public static DockerAzureHostedSnapshot From(IServiceProvider serviceProvider)
    {
        return new DockerAzureHostedSnapshot(
            serviceProvider.GetRequiredService<ConfigStore<StorageAccountConfig>>().GetConfig("storage"),
            serviceProvider.GetRequiredService<ConfigStore<CosmosContainerDbConfig>>().GetConfig("cosmos"),
            serviceProvider.GetRequiredService<ConfigStore<SqlDatabaseConfig>>().GetConfig("sql"),
            serviceProvider.GetRequiredService<ConfigStore<ServiceBusConfig>>().Snapshot(),
            serviceProvider.GetRequiredService<ConfigStore<FunctionAppConfig>>().Snapshot(),
            serviceProvider.GetRequiredService<ConfigStore<LogicAppConfig>>().Snapshot());
    }
}

internal static class DockerAzureSmokeServiceProviderFactory
{
    public static ServiceProvider CreateBootstrapProvider()
        => Create(
            new DockerAzureHostedSnapshot(
                new StorageAccountConfig
                {
                    ConnectionString = "UseDevelopmentStorage=true",
                    QueueContainerName = null,
                    BlobContainerName = "smoke-blob",
                    TableContainerName = DockerAzureEnvironmentSmokeTests.SmokeTableName,
                },
                new CosmosContainerDbConfig
                {
                    ConnectionString = "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;",
                    DatabaseName = "smoke-db",
                    ContainerName = "smoke-container",
                },
                new SqlDatabaseConfig
                {
                    ConnectionString = "Server=localhost;Database=master;User Id=sa;Password=Your_password123;TrustServerCertificate=True",
                    DatabaseName = "master",
                },
                new Dictionary<string, ServiceBusConfig>
                {
                    ["bus"] = CreateServiceBusConfig("default-queue", null, null),
                    ["func-trigger-bus"] = CreateServiceBusConfig(null, "smoke-trigger-topic", "smoke-trigger-subscription"),
                    ["func-reply-bus"] = CreateServiceBusConfig(null, "smoke-reply-topic", "smoke-reply-default"),
                },
                new Dictionary<string, FunctionAppConfig>
                {
                    ["func-sb"] = CreateFunctionAppConfig(),
                    ["func"] = CreateFunctionAppConfig(),
                },
                new Dictionary<string, LogicAppConfig>
                {
                    ["logic"] = new()
                    {
                        WorkflowName = "SmokeWorkflow",
                        Standard = new LogicAppStandardConfig
                        {
                            BaseUrl = "http://localhost/",
                        },
                    },
                }),
            withFunctionApp: true);

    public static ServiceProvider Create(DockerAzureHostedSnapshot snapshot, bool withFunctionApp)
    {
        ServiceCollection services = new();

        services.AddSingleton(ConfigStore<StorageAccountConfig>.Create("storage", snapshot.Storage));
        services.AddSingleton(ConfigStore<CosmosContainerDbConfig>.Create("cosmos", snapshot.Cosmos));
        services.AddSingleton(ConfigStore<SqlDatabaseConfig>.Create("sql", snapshot.Sql));

        ConfigStore<ServiceBusConfig> serviceBusStore = ConfigStore<ServiceBusConfig>.Create("bus", snapshot.ServiceBus["bus"]);
        serviceBusStore.AddConfig("func-trigger-bus", snapshot.ServiceBus["func-trigger-bus"]);
        serviceBusStore.AddConfig("func-reply-bus", snapshot.ServiceBus["func-reply-bus"]);
        services.AddSingleton(serviceBusStore);

        ConfigStore<FunctionAppConfig> functionAppStore = ConfigStore<FunctionAppConfig>.Create("func-sb", snapshot.FunctionApps["func-sb"]);
        if (withFunctionApp)
            functionAppStore.AddConfig("func", snapshot.FunctionApps["func"]);

        services.AddSingleton(functionAppStore);

        services.AddSingleton(ConfigStore<LogicAppConfig>.Create("logic", snapshot.LogicApps["logic"]));
        services.ConfigureDockerAzureCosmosEmulator();
        services.AddDbContext<DockerAzureEnvironmentSmokeTests.SmokeSqlDbContext>((serviceProvider, options) =>
        {
            SqlDatabaseConfig config = serviceProvider.GetRequiredService<ConfigStore<SqlDatabaseConfig>>().GetConfig("sql");
            options.UseSqlServer(config.ConnectionString);
        });
        services.AddSqlArtifactContexts(registry => registry.AddForIdentifier<DockerAzureEnvironmentSmokeTests.SmokeSqlDbContext>("sql"));

        return services.BuildServiceProvider();
    }

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
