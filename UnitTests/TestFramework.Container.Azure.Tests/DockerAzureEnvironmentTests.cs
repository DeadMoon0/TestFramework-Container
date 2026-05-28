using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using System.Reflection;
using TestFramework.Azure.DB.CosmosDB;
using TestFramework.Azure.FunctionApp;
using TestFramework.Azure.Configuration;
using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Azure.DB.SqlServer;
using TestFramework.Azure.Identifier;
using TestFramework.Azure.ServiceBus;
using TestFramework.Azure.StorageAccount.Blob;
using TestFramework.Azure.StorageAccount.Table;
using TestFramework.Azure.Trigger.IsLive;
using TestFramework.Container.Azure;
using TestFramework.Container.Azure.Contracts;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Debugger;
using TestFramework.Core.Environment;
using TestFramework.Core.Logging;
using TestFramework.Core.Steps;
using TestFramework.Core.Variables;

namespace TestFramework.Container.Azure.Tests;

public class DockerAzureEnvironmentTests
{
    [Fact]
    public void ResolveComponents_MapsArtifactsToDockerAzureComponents()
    {
        DockerAzureEnvironment environment = new();
        ArtifactInstanceGeneric[] artifacts =
        [
            CreateArtifactInstance<StorageAccountBlobArtifactDescriber, StorageAccountBlobArtifactData, StorageAccountBlobArtifactReference>(
                new StorageAccountBlobArtifactDescriber(),
                "blob",
                new StorageAccountBlobArtifactReference("storage", Var.Const("path")),
                new StorageAccountBlobArtifactData([], [])),
            CreateArtifactInstance<CosmosDbItemArtifactDescriber<TestCosmosItem>, CosmosDbItemArtifactData<TestCosmosItem>, CosmosDbItemArtifactReference<TestCosmosItem>>(
                new CosmosDbItemArtifactDescriber<TestCosmosItem>(),
                "cosmos",
                new CosmosDbItemArtifactReference<TestCosmosItem>("cosmos", Var.Const(new Microsoft.Azure.Cosmos.PartitionKey("tenant-1")), Var.Const("id")),
                new CosmosDbItemArtifactData<TestCosmosItem>(new TestCosmosItem("id", "tenant-1"))),
            CreateArtifactInstance<SqlRowArtifactDescriber<TestRow>, SqlRowArtifactData<TestRow>, SqlRowArtifactReference<TestRow>>(
                new SqlRowArtifactDescriber<TestRow>(),
                "sql",
                new SqlRowArtifactReference<TestRow>("sql", Var.Const("42")),
                new SqlRowArtifactData<TestRow>(new TestRow()))
        ];

        IReadOnlyCollection<EnvComponentIdentifier> result = environment.ResolveComponents(artifacts, []);

        Assert.Contains(DockerAzureEnvironment.AzuriteComponentId, result);
        Assert.Contains(DockerAzureEnvironment.CosmosDbComponentId, result);
        Assert.Contains(DockerAzureEnvironment.MsSqlComponentId, result);
        Assert.Contains("storage", environment.UsedStorageIdentifiers);
        Assert.Contains("cosmos", environment.UsedCosmosIdentifiers);
        Assert.Contains("sql", environment.UsedSqlIdentifiers);

        Dictionary<string, string> cosmosPartitionKeyPaths = (Dictionary<string, string>)typeof(DockerAzureEnvironment)
            .GetProperty("CosmosPartitionKeyPaths", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(environment)!;
        Assert.Equal("/PartitionKey", cosmosPartitionKeyPaths["cosmos"]);
    }

    [Fact]
    public void ResolveComponents_IncludeOnlyDoesNotForceServiceBusComponent()
    {
        DockerAzureEnvironment environment = DockerAzureEnvironment.For<TestServiceBusDefinition>();

        IReadOnlyCollection<EnvComponentIdentifier> result = environment.ResolveComponents([], []);

        Assert.DoesNotContain(DockerAzureEnvironment.ServiceBusComponentId, result);
        Assert.DoesNotContain("bus", environment.UsedServiceBusIdentifiers);
    }

    [Fact]
    public void ResolveComponents_MapsOpenGenericTableArtifactsToAzurite()
    {
        DockerAzureEnvironment environment = new();
        ArtifactInstanceGeneric[] artifacts =
        [
            CreateArtifactInstance<TableStorageEntityArtifactDescriber<TestTableEntity>, TableStorageEntityArtifactData<TestTableEntity>, TableStorageEntityArtifactReference<TestTableEntity>>(
                new TableStorageEntityArtifactDescriber<TestTableEntity>(),
                "table",
                new TableStorageEntityArtifactReference<TestTableEntity>("table-storage", Var.Const("table"), Var.Const("pk"), Var.Const("rk")),
                new TableStorageEntityArtifactData<TestTableEntity>(new TestTableEntity()))
        ];

        IReadOnlyCollection<EnvComponentIdentifier> result = environment.ResolveComponents(artifacts, []);

        Assert.Contains(DockerAzureEnvironment.AzuriteComponentId, result);
        Assert.Contains("table-storage", environment.UsedStorageIdentifiers);
    }

    [Fact]
    public void ResolveComponents_MapsServiceBusStepRequirementsWithoutArtifacts()
    {
        DockerAzureEnvironment environment = new();
        ServiceBusSendTrigger trigger = new("bus", Var.Const(new ServiceBusMessage("payload")));

        IReadOnlyCollection<EnvComponentIdentifier> result = environment.ResolveComponents([], ((IHasEnvironmentRequirements)trigger).GetEnvironmentRequirements(null!));

        Assert.Contains(DockerAzureEnvironment.ServiceBusComponentId, result);
        Assert.Contains("bus", environment.UsedServiceBusIdentifiers);
    }

    [Fact]
    public void ResolveComponents_MapsIsLiveStepRequirementsWithoutArtifacts()
    {
        DockerAzureEnvironment environment = DockerAzureEnvironment.For<MinimalFunctionAppDefinition>();
        var functionStep = new IsLiveTrigger().FunctionApp("func");
        var blobStep = new IsLiveTrigger().Blob("storage");
        var cosmosStep = new IsLiveTrigger().Cosmos("cosmos");
        var sqlStep = new IsLiveTrigger().Sql("sql");

        List<EnvironmentRequirement> requirements = [];
        requirements.AddRange(((IHasEnvironmentRequirements)functionStep).GetEnvironmentRequirements(null!));
        requirements.AddRange(((IHasEnvironmentRequirements)blobStep).GetEnvironmentRequirements(null!));
        requirements.AddRange(((IHasEnvironmentRequirements)cosmosStep).GetEnvironmentRequirements(null!));
        requirements.AddRange(((IHasEnvironmentRequirements)sqlStep).GetEnvironmentRequirements(null!));

        IReadOnlyCollection<EnvComponentIdentifier> result = environment.ResolveComponents([], requirements);

        Assert.Contains(DockerAzureEnvironment.FunctionAppComponentId, result);
        Assert.Contains(DockerAzureEnvironment.AzuriteComponentId, result);
        Assert.Contains(DockerAzureEnvironment.CosmosDbComponentId, result);
        Assert.Contains(DockerAzureEnvironment.MsSqlComponentId, result);
        Assert.Contains("func", environment.UsedFunctionAppIdentifiers);
    }

    [Fact]
    public void ResolveComponents_ForAddsTypedFunctionAppDefinitionsAndDependencies()
    {
        DockerAzureEnvironment environment = DockerAzureEnvironment.For<TestFunctionAppDefinition>();
        var functionStep = new IsLiveTrigger().FunctionApp("func");

        IReadOnlyCollection<EnvComponentIdentifier> result = environment.ResolveComponents([], ((IHasEnvironmentRequirements)functionStep).GetEnvironmentRequirements(null!));

        Assert.Contains(DockerAzureEnvironment.FunctionAppComponentId, result);
        Assert.Contains(DockerAzureEnvironment.AzuriteComponentId, result);
        Assert.Contains(DockerAzureEnvironment.CosmosDbComponentId, result);
        Assert.Contains(DockerAzureEnvironment.ServiceBusComponentId, result);
        Assert.Contains("func", environment.UsedFunctionAppIdentifiers);
        Assert.Contains("storage", environment.UsedStorageIdentifiers);
        Assert.Contains("cosmos", environment.UsedCosmosIdentifiers);
        Assert.Contains("bus", environment.UsedServiceBusIdentifiers);

        IReadOnlyCollection<DockerFunctionAppRegistration> registrations = (IReadOnlyCollection<DockerFunctionAppRegistration>)typeof(DockerAzureEnvironment)
            .GetMethod("GetFunctionAppRegistrations", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(environment, [])!;
        DockerFunctionAppRegistration registration = Assert.Single(registrations);
        Assert.Equal("func", registration.Identifier);
        Assert.Equal(typeof(TestFunctionHost), registration.FunctionType);
    }

    [Fact]
    public void ForFunctionAppWithCommonBindings_AddsCommonLocalStackWithoutCustomDefinition()
    {
        DockerAzureEnvironment environment = DockerAzureEnvironment.ForFunctionAppWithCommonBindings<TestFunctionHost, TestStorageDefinition, TestCosmosDefinition, TestServiceBusDefinition>("func-inline");
        var functionStep = new IsLiveTrigger().FunctionApp("func-inline");

        IReadOnlyCollection<EnvComponentIdentifier> result = environment.ResolveComponents([], ((IHasEnvironmentRequirements)functionStep).GetEnvironmentRequirements(null!));

        Assert.Contains(DockerAzureEnvironment.FunctionAppComponentId, result);
        Assert.Contains(DockerAzureEnvironment.AzuriteComponentId, result);
        Assert.Contains(DockerAzureEnvironment.CosmosDbComponentId, result);
        Assert.Contains(DockerAzureEnvironment.ServiceBusComponentId, result);
        Assert.Contains("func-inline", environment.UsedFunctionAppIdentifiers);
    }

    [Fact]
    public void ResolveComponents_FormatsResolutionSummaryWithIdentifiersAndContracts()
    {
        DockerAzureEnvironment environment = DockerAzureEnvironment.For<ContractLoggingFunctionAppDefinition>();
        var functionStep = new IsLiveTrigger().FunctionApp("func-contract");

        environment.ResolveComponents([], ((IHasEnvironmentRequirements)functionStep).GetEnvironmentRequirements(null!));

        RecordingRunDebugger debugger = new();
        ScopedLogger logger = CreateLogger(debugger);
        typeof(DockerAzureEnvironment)
            .GetMethod("LogPendingResolutionSummary", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(environment, [logger]);

        Assert.Contains(debugger.LogEntries, entry => entry.Message.Contains("Docker Azure resolution: components", StringComparison.Ordinal));
        Assert.Contains(debugger.LogEntries, entry => entry.Message.Contains("Function Apps: func-contract", StringComparison.Ordinal));
        Assert.Contains(debugger.LogEntries, entry => entry.Message.Contains("Service Bus: bus", StringComparison.Ordinal));
        Assert.Contains(debugger.LogEntries, entry => entry.Message.Contains("functionapp:func-contract <= servicebus:bus", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveComponents_ForAppliesServiceBusTopologyPathFromDependencies()
    {
        DockerAzureEnvironment environment = DockerAzureEnvironment.For<TestFunctionAppDefinition>();
        var functionStep = new IsLiveTrigger().FunctionApp("func");

        IReadOnlyCollection<EnvComponentIdentifier> result = environment.ResolveComponents([], ((IHasEnvironmentRequirements)functionStep).GetEnvironmentRequirements(null!));

        Assert.Contains(DockerAzureEnvironment.ServiceBusComponentId, result);
        Assert.Contains("bus", environment.UsedServiceBusIdentifiers);

        string topologyPath = (string)typeof(DockerAzureEnvironment)
            .GetMethod("GetServiceBusTopologyConfigPath", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(environment, [])!;
        Assert.Equal(Path.Combine("TestTopology", "servicebus.json"), topologyPath);
    }

    [Fact]
    public void FunctionAppEnvComponent_BuildsAppSettingsFromDefinitionResourceBindings()
    {
        DockerAzureEnvironment environment = DockerAzureEnvironment.For<TestFunctionAppDefinition>();

        object descriptor = typeof(DockerAzureEnvironment)
            .GetMethod("GetRequiredFunctionAppDescriptor", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(environment, [new FunctionAppIdentifier("func")])!;

        ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton(ConfigStore<StorageAccountConfig>.Create("storage", new StorageAccountConfig
            {
                ConnectionString = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=key=;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;QueueEndpoint=http://127.0.0.1:10001/devstoreaccount1;TableEndpoint=http://127.0.0.1:10002/devstoreaccount1;",
                QueueContainerName = null,
                BlobContainerName = "blob-container",
                TableContainerName = "table-container"
            }))
            .AddSingleton(ConfigStore<CosmosContainerDbConfig>.Create("cosmos", new CosmosContainerDbConfig
            {
                ConnectionString = "AccountEndpoint=https://localhost:8081/;AccountKey=key=;",
                DatabaseName = "test-db",
                ContainerName = "test-container"
            }))
            .AddSingleton(ConfigStore<ServiceBusConfig>.Create("bus", new ServiceBusConfig
            {
                ConnectionString = "Endpoint=sb://localhost/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=key=;",
                QueueName = null,
                TopicName = "processing-topic",
                SubscriptionName = "processing-subscription",
                RequiredSession = false
            }))
            .BuildServiceProvider();

        Dictionary<string, string> settings = (Dictionary<string, string>)typeof(DockerAzureEnvironment).Assembly
            .GetType("TestFramework.Container.Azure.Components.FunctionAppEnvComponent")!
            .GetMethod("BuildAppSettings", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [environment, serviceProvider, descriptor, null])!;

        Assert.Contains(DockerAzureEnvironment.AzuriteNetworkAlias, settings["StorageAccountConnectionString"]);
        Assert.Equal(settings["StorageAccountConnectionString"], settings["AzureWebJobsStorage"]);
        Assert.DoesNotContain("accountkey=\"", settings["StorageAccountConnectionString"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("table-container", settings["StorageTableName"]);
        Assert.Contains(DockerAzureEnvironment.CosmosDbNetworkAlias, settings["CosmosDbConnectionString"]);
        Assert.DoesNotContain("accountkey=\"", settings["CosmosDbConnectionString"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("test-db", settings["CosmosDatabaseName"]);
        Assert.Equal("test-container", settings["CosmosContainerName"]);
        Assert.Contains($"endpoint=sb://{DockerAzureEnvironment.ServiceBusNetworkAlias}/", settings["ServiceBusTriggerConnection"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("processing-topic", settings["ServiceBusTriggerTopicName"]);
        Assert.Equal("processing-subscription", settings["ServiceBusTriggerSubscriptionName"]);
        Assert.Contains($"endpoint=sb://{DockerAzureEnvironment.ServiceBusNetworkAlias}/", settings["ServiceBusReplyConnectionString"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("processing-topic", settings["ServiceBusReplyTopicName"]);
        Assert.DoesNotContain(FunctionAppResourceSettingNames.StorageIdentifier, settings.Keys);
        Assert.DoesNotContain(FunctionAppResourceSettingNames.CosmosIdentifier, settings.Keys);
        Assert.DoesNotContain(FunctionAppResourceSettingNames.ServiceBusTriggerIdentifier, settings.Keys);
        Assert.DoesNotContain(FunctionAppResourceSettingNames.ServiceBusReplyIdentifier, settings.Keys);
    }

    [Fact]
    public void GetOrCreateConfigStore_SynthesizesDefinitionDefaults_WhenStoreWasNotRegistered()
    {
        DockerAzureEnvironment environment = DockerAzureEnvironment.For<SynthesizedFunctionAppDefinition>();
        var functionStep = new IsLiveTrigger().FunctionApp("auto-func");

        environment.ResolveComponents([], ((IHasEnvironmentRequirements)functionStep).GetEnvironmentRequirements(null!));

        ServiceProvider serviceProvider = new ServiceCollection().BuildServiceProvider();

        ConfigStore<FunctionAppConfig> functionStore = (ConfigStore<FunctionAppConfig>)typeof(DockerAzureEnvironment)
            .GetMethod("GetOrCreateConfigStore", BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(FunctionAppConfig))
            .Invoke(environment, [serviceProvider, environment.UsedFunctionAppIdentifiers, "Function App environment setup"])!;

        FunctionAppConfig config = functionStore.GetConfig("auto-func");
        Assert.Equal("http://localhost/", config.BaseUrl);
        Assert.Equal("unused", config.Code);
        Assert.Equal("unused", config.AdminCode);
    }

    [Fact]
    public void CreateRunScopedServiceProvider_ResolvesSynthesizedCosmosStore_ForActivatedIdentifiers()
    {
        DockerAzureEnvironment environment = DockerAzureEnvironment.For<DefaultedCosmosDefinition>();
        ArtifactInstanceGeneric[] artifacts =
        [
            CreateArtifactInstance<CosmosDbItemArtifactDescriber<TestCosmosItem>, CosmosDbItemArtifactData<TestCosmosItem>, CosmosDbItemArtifactReference<TestCosmosItem>>(
                new CosmosDbItemArtifactDescriber<TestCosmosItem>(),
                "cosmos-artifact",
                new CosmosDbItemArtifactReference<TestCosmosItem>("cosmos-default", Var.Const(new Microsoft.Azure.Cosmos.PartitionKey("tenant-1")), Var.Const("id")),
                new CosmosDbItemArtifactData<TestCosmosItem>(new TestCosmosItem("id", "tenant-1")))
        ];

        environment.ResolveComponents(artifacts, []);

        IServiceProvider runServiceProvider = ((IRunScopedServiceProviderFactory)environment)
            .CreateRunScopedServiceProvider(new ServiceCollection().BuildServiceProvider());

        ConfigStore<CosmosContainerDbConfig> cosmosStore = runServiceProvider.GetRequiredService<ConfigStore<CosmosContainerDbConfig>>();
        CosmosContainerDbConfig config = cosmosStore.GetConfig("cosmos-default");

        Assert.Equal("test-db", config.DatabaseName);
        Assert.Equal("test-container", config.ContainerName);
    }

    [Fact]
    public void DockerEndpointMap_StorageForContainer_RewritesEndpointsAndUnquotesAccountKey()
    {
        string connectionString = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=key=;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;QueueEndpoint=http://127.0.0.1:10001/devstoreaccount1;TableEndpoint=http://127.0.0.1:10002/devstoreaccount1;";

        object endpointMap = Activator.CreateInstance(typeof(DockerAzureEnvironment).Assembly
            .GetType("TestFramework.Container.Azure.DockerEndpointMap")!, nonPublic: true)!;

        string rewritten = endpointMap.GetType()
            .GetMethod("RewriteStorageForContainer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(endpointMap, [connectionString])!
            .ToString()!;

        Assert.Contains($"BlobEndpoint=http://{DockerAzureEnvironment.AzuriteNetworkAlias}:10000", rewritten, StringComparison.Ordinal);
        Assert.Contains($"QueueEndpoint=http://{DockerAzureEnvironment.AzuriteNetworkAlias}:10001", rewritten, StringComparison.Ordinal);
        Assert.Contains($"TableEndpoint=http://{DockerAzureEnvironment.AzuriteNetworkAlias}:10002", rewritten, StringComparison.Ordinal);
        Assert.Contains("DefaultEndpointsProtocol=http", rewritten, StringComparison.Ordinal);
        Assert.Contains("AccountName=devstoreaccount1", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("accountkey=\"", rewritten, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DockerEndpointMap_CosmosForContainer_RewritesEndpointAndUnquotesAccountKey()
    {
        string connectionString = "AccountEndpoint=https://localhost:8081/;AccountKey=key=;";

        object endpointMap = Activator.CreateInstance(typeof(DockerAzureEnvironment).Assembly
            .GetType("TestFramework.Container.Azure.DockerEndpointMap")!, nonPublic: true)!;

        string rewritten = endpointMap.GetType()
            .GetMethod("RewriteCosmosForContainer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(endpointMap, [connectionString])!
            .ToString()!;

        Assert.Contains($"AccountEndpoint=https://{DockerAzureEnvironment.CosmosDbNetworkAlias}:8081/", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("accountkey=\"", rewritten, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DockerEndpointMap_ServiceBusForContainer_UsesAliasAndContainerPort()
    {
        string connectionString = "Endpoint=sb://localhost:19123/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=key=;UseDevelopmentEmulator=true;";

        object endpointMap = Activator.CreateInstance(typeof(DockerAzureEnvironment).Assembly
            .GetType("TestFramework.Container.Azure.DockerEndpointMap")!, nonPublic: true)!;

        string rewritten = endpointMap.GetType()
            .GetMethod("RewriteServiceBusForContainer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(endpointMap, [connectionString])!
            .ToString()!;

        Assert.Contains($"Endpoint=sb://{DockerAzureEnvironment.ServiceBusNetworkAlias}/", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain(":19123/", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("amqp://", rewritten, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UseDevelopmentEmulator=true", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionStringGuards_AcceptsValidServiceBusEmulatorConnectionString()
    {
        InvokeEnsureServiceBus("Endpoint=sb://localhost/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=key=;UseDevelopmentEmulator=true;");
    }

    [Fact]
    public void ConnectionStringGuards_RejectsNonLocalTargets()
    {
        AssertInnerInvalidOperation(() => InvokeEnsureAzurite("AccountName=devstoreaccount1;BlobEndpoint=https://prod.example/;"));
        AssertInnerInvalidOperation(() => InvokeEnsureServiceBus("Endpoint=sb://prod.servicebus.windows.net/;"));
        AssertInnerInvalidOperation(() => InvokeEnsureCosmos("AccountEndpoint=https://prod.documents.azure.com:443/;AccountKey=abc;"));
        AssertInnerInvalidOperation(() => InvokeEnsureSql("Server=tcp:prod.database.windows.net,1433;Database=main;User Id=sa;Password=Secret123!;TrustServerCertificate=True"));
    }

    [Fact]
    public void ServiceBusConfigLocator_ResolvesOutputRelativeFile()
    {
        string relativePath = Path.Combine("Configurations", "ServiceBus", "config.json");

        string resolved = typeof(DockerAzureEnvironment).Assembly
            .GetType("TestFramework.Container.Azure.ServiceBusConfigLocator")!
            .GetMethod("Resolve", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [relativePath])!
            .ToString()!;

        Assert.True(File.Exists(resolved));
    }

    [Fact]
    public void ServiceBusConfigLocator_ResolvesLegacyAzureDockerRelativeFile()
    {
        string relativePath = Path.Combine("AzureDocker", "Configurations", "ServiceBus", "config.json");

        string resolved = typeof(DockerAzureEnvironment).Assembly
            .GetType("TestFramework.Container.Azure.ServiceBusConfigLocator")!
            .GetMethod("Resolve", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [relativePath])!
            .ToString()!;

        Assert.True(File.Exists(resolved));
        Assert.EndsWith(Path.Combine("Configurations", "ServiceBus", "config.json"), resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveComponents_ThrowsWhenFunctionAppRegistrationIsMissing()
    {
        DockerAzureEnvironment environment = new();
        var functionStep = new IsLiveTrigger().FunctionApp("func");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => environment.ResolveComponents([], ((IHasEnvironmentRequirements)functionStep).GetEnvironmentRequirements(null!)));

        Assert.Contains("func", exception.Message);
    }

    [Fact]
    public void FunctionAppEnvComponent_CreateMissingFunctionAppOutputException_BuildsHelpfulMessage()
    {
        Type componentType = typeof(DockerAzureEnvironment).Assembly.GetType("TestFramework.Container.Azure.Components.FunctionAppEnvComponent", throwOnError: true)!;

        InvalidOperationException actual = (InvalidOperationException)componentType
            .GetMethod("CreateMissingFunctionAppOutputException", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [typeof(DockerAzureEnvironmentTests), "TestFramework.Container.Azure.Tests", "C:\\repo\\project", "C:\\repo\\project\\bin\\Debug\\net8.0", "C:\\repo\\project\\bin\\Release\\net8.0"])!;

        Assert.Contains("host.json", actual.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Build or publish", actual.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveComponents_ThrowsWhenLogicAppRequirementIsUsed()
    {
        DockerAzureEnvironment environment = new();
        var logicAppStep = new IsLiveTrigger().LogicApp("logic");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => environment.ResolveComponents([], ((IHasEnvironmentRequirements)logicAppStep).GetEnvironmentRequirements(null!)));

        Assert.Contains("no longer supports Logic App", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveComponents_ThrowsWhenCosmosArtifactsDisagreeOnPartitionKeyPath()
    {
        DockerAzureEnvironment environment = new();
        ArtifactInstanceGeneric[] artifacts =
        [
            CreateArtifactInstance<CosmosDbItemArtifactDescriber<TestCosmosItem>, CosmosDbItemArtifactData<TestCosmosItem>, CosmosDbItemArtifactReference<TestCosmosItem>>(
                new CosmosDbItemArtifactDescriber<TestCosmosItem>(),
                "first",
                new CosmosDbItemArtifactReference<TestCosmosItem>("cosmos", Var.Const(new Microsoft.Azure.Cosmos.PartitionKey("tenant-1")), Var.Const("id-1")),
                new CosmosDbItemArtifactData<TestCosmosItem>(new TestCosmosItem("id-1", "tenant-1"))),
            CreateArtifactInstance<CosmosDbItemArtifactDescriber<TestCosmosItemAlternatePartition>, CosmosDbItemArtifactData<TestCosmosItemAlternatePartition>, CosmosDbItemArtifactReference<TestCosmosItemAlternatePartition>>(
                new CosmosDbItemArtifactDescriber<TestCosmosItemAlternatePartition>(),
                "second",
                new CosmosDbItemArtifactReference<TestCosmosItemAlternatePartition>("cosmos", Var.Const(new Microsoft.Azure.Cosmos.PartitionKey("tenant-2")), Var.Const("id-2")),
                new CosmosDbItemArtifactData<TestCosmosItemAlternatePartition>(new TestCosmosItemAlternatePartition("id-2", "tenant-2")))
        ];

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => environment.ResolveComponents(artifacts, []));

        Assert.Contains("conflicting partition key paths", exception.Message);
    }

    [Fact]
    public async Task AzuriteEnvComponent_ThrowsWhenStorageIdentifiersAreUsedWithoutConfigStore()
    {
        DockerAzureEnvironment environment = DockerAzureEnvironment.For<TestStorageDefinition>();
        var blobStep = new IsLiveTrigger().Blob("storage");

        environment.ResolveComponents([], ((IHasEnvironmentRequirements)blobStep).GetEnvironmentRequirements(null!));
        typeof(DockerAzureEnvironment)
            .GetMethod("SetRuntimeState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(environment, [DockerAzureEnvironment.NetworkComponentId, new StubNetwork()]);

        object component = Activator.CreateInstance(typeof(DockerAzureEnvironment).Assembly.GetType("TestFramework.Container.Azure.Components.AzuriteEnvComponent")!, true)!;
        Task createTask = (Task)component.GetType().GetMethod("CreateAsync")!.Invoke(component,
        [environment, new ServiceCollection().BuildServiceProvider(), null!, null!, null!, CancellationToken.None])!;

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await createTask);
        Assert.Contains("ConfigStore<StorageAccountConfig>", exception.Message);
    }

    [Fact]
    public void ResolveComponents_IncludeAppliesInfrastructureOverrides()
    {
        DockerAzureEnvironment environment = DockerAzureEnvironment.For<TestDefaultServiceBusDefinition>()
            .Include<TestInfrastructureDefinition>();
        ServiceBusSendTrigger trigger = new("bus", Var.Const(new ServiceBusMessage("payload")));

        IReadOnlyCollection<EnvComponentIdentifier> result = environment.ResolveComponents([], ((IHasEnvironmentRequirements)trigger).GetEnvironmentRequirements(null!));

        Assert.Contains(DockerAzureEnvironment.ServiceBusComponentId, result);
        Assert.Equal("custom/azurite:1", typeof(DockerAzureEnvironment).GetMethod("GetAzuriteImage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(environment, []));
        Assert.Equal("custom/cosmos:1", typeof(DockerAzureEnvironment).GetMethod("GetCosmosDbImage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(environment, []));
        Assert.Equal("custom/mssql:1", typeof(DockerAzureEnvironment).GetMethod("GetMsSqlImage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(environment, []));
        Assert.Equal(1024, typeof(DockerAzureEnvironment).GetMethod("GetMsSqlMemoryLimitMb", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(environment, []));
        Assert.Equal("custom/servicebus:1", typeof(DockerAzureEnvironment).GetMethod("GetServiceBusImage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(environment, []));
        Assert.Equal("StrongerPassword_123!", typeof(DockerAzureEnvironment).GetMethod("GetMsSqlPassword", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(environment, []));
        Assert.Equal(Path.Combine("Infrastructure", "bus-topology.json"), typeof(DockerAzureEnvironment).GetMethod("GetServiceBusTopologyConfigPath", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(environment, []));
    }

    [Fact]
    public void Include_ThrowsWhenInfrastructureOverridesConflict()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => DockerAzureEnvironment.For<TestInfrastructureDefinition>()
            .Include<ConflictingInfrastructureDefinition>());

        Assert.Contains(nameof(DockerAzureInfrastructureDefinition.AzuriteImage), exception.Message);
    }

    [Fact]
    public void Include_ThrowsWhenInfrastructureTopologyConflictsWithCustomServiceBusTopology()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => DockerAzureEnvironment.For<TestInfrastructureDefinition>()
            .Include<ConflictingTopologyServiceBusDefinition>());

        Assert.Contains("Multiple Service Bus topology sources", exception.Message);
    }

    [Fact]
    public void ResolveComponents_MaterializesFluentServiceBusTopology()
    {
        DockerAzureEnvironment environment = DockerAzureEnvironment.For<FluentServiceBusDefinition>();
        ServiceBusSendTrigger trigger = new("bus", Var.Const(new ServiceBusMessage("payload")));

        IReadOnlyCollection<EnvComponentIdentifier> result = environment.ResolveComponents([], ((IHasEnvironmentRequirements)trigger).GetEnvironmentRequirements(null!));

        Assert.Contains(DockerAzureEnvironment.ServiceBusComponentId, result);

        object topologySource = typeof(DockerAzureEnvironment).GetMethod("GetServiceBusTopologySource", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(environment, [])!;
        object materialized = typeof(DockerAzureEnvironment).Assembly
            .GetType("TestFramework.Container.Azure.ServiceBusTopologyMaterializer")!
            .GetMethod("Materialize", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [topologySource])!;

        string configPath = (string)materialized.GetType().GetProperty("ConfigPath")!.GetValue(materialized)!;
        bool isTemporary = (bool)materialized.GetType().GetProperty("IsTemporary")!.GetValue(materialized)!;

        try
        {
            string json = File.ReadAllText(configPath);
            Assert.Contains("fluent-topic", json, StringComparison.Ordinal);
            Assert.Contains("fluent-subscription", json, StringComparison.Ordinal);
        }
        finally
        {
            if (isTemporary && File.Exists(configPath))
                File.Delete(configPath);
        }
    }

    private static void InvokeEnsureAzurite(string connectionString)
        => typeof(DockerAzureEnvironment).Assembly
            .GetType("TestFramework.Container.Azure.ConnectionStringGuards")!
            .GetMethod("EnsureAzurite", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [connectionString]);

    private static void InvokeEnsureServiceBus(string connectionString)
        => typeof(DockerAzureEnvironment).Assembly
            .GetType("TestFramework.Container.Azure.ConnectionStringGuards")!
            .GetMethod("EnsureServiceBus", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [connectionString]);

    private static void InvokeEnsureCosmos(string connectionString)
        => typeof(DockerAzureEnvironment).Assembly
            .GetType("TestFramework.Container.Azure.ConnectionStringGuards")!
            .GetMethod("EnsureCosmos", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [connectionString]);

    private static void InvokeEnsureSql(string connectionString)
        => typeof(DockerAzureEnvironment).Assembly
            .GetType("TestFramework.Container.Azure.ConnectionStringGuards")!
            .GetMethod("EnsureSql", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [connectionString]);

    private sealed class TestRow;

    private sealed class TestStorageDefinition : DockerStorageDefinition
    {
        public override StorageAccountIdentifier Identifier => "storage";
    }

    private sealed class TestCosmosDefinition : DockerCosmosDefinition<TestCosmosItem>
    {
        public override CosmosContainerIdentifier Identifier => "cosmos";
    }

    private sealed class DefaultedCosmosDefinition : DockerCosmosDefinition<TestCosmosItem>
    {
        public override CosmosContainerIdentifier Identifier => "cosmos-default";

            protected override string? DatabaseName => "test-db";
            protected override string? ContainerName => "test-container";
    }

    private sealed class TestServiceBusDefinition : DockerServiceBusDefinition
    {
        public override ServiceBusIdentifier Identifier => "bus";

        public override string TopologyConfigPath => Path.Combine("TestTopology", "servicebus.json");
    }

    private sealed class TestDefaultServiceBusDefinition : DockerServiceBusDefinition
    {
        public override ServiceBusIdentifier Identifier => "bus";
    }

    private sealed class FluentServiceBusDefinition : DockerServiceBusDefinition
    {
        public override ServiceBusIdentifier Identifier => "bus";

        protected override void ConfigureServiceBusTopology(DockerServiceBusTopologyBuilder builder)
        {
            builder.AddNamespace("sbemulatorns", ns => ns
                .AddTopic("fluent-topic", topic => topic.AddSubscription("fluent-subscription")));
        }
    }

    private sealed class TestFunctionHost;

    private sealed class MinimalFunctionAppDefinition : DockerFunctionAppDefinition<DockerAzureEnvironmentTests>
    {
        public override FunctionAppIdentifier Identifier => "func";
    }

    private sealed class TestFunctionAppDefinition : DockerFunctionAppDefinition<TestFunctionHost>
    {
        public override FunctionAppIdentifier Identifier => "func";

        protected override void Configure(DockerFunctionAppBuilder builder)
        {
            builder
                .UseStorage<TestStorageDefinition>()
                .UseCosmos<TestCosmosDefinition>()
                .UseServiceBusTrigger<TestServiceBusDefinition>()
                .UseServiceBusReply<TestServiceBusDefinition>();
        }
    }

    private sealed class ContractLoggingFunctionAppDefinition : DockerFunctionAppDefinition<TestFunctionHost>
    {
        public override FunctionAppIdentifier Identifier => "func-contract";

        protected override void ConfigureDependencies(DockerAzureDependencyBuilder dependencies)
        {
            dependencies.Include<ContractLoggingServiceBusDefinition>();
        }

        protected override void ConfigureContracts(DockerAzureContractBuilder contracts)
        {
            contracts.Require(new ServiceBusEndpointContract(
                ContractKey: "reply",
                ServiceBusIdentifier: "bus",
                EndpointKind: ServiceBusEndpointKind.TopicSubscription,
                EntityName: "processing-topic",
                SubscriptionName: "processing-subscription"));
        }
    }

    private sealed class ContractLoggingServiceBusDefinition : DockerServiceBusDefinition
    {
        public override ServiceBusIdentifier Identifier => "bus";

        protected override void ConfigureContracts(DockerAzureContractBuilder contracts)
        {
            contracts.Provide(new ServiceBusEndpointContract(
                ContractKey: "reply",
                ServiceBusIdentifier: Identifier,
                EndpointKind: ServiceBusEndpointKind.TopicSubscription,
                EntityName: "processing-topic",
                SubscriptionName: "processing-subscription"));
        }
    }

    private sealed class SynthesizedFunctionAppDefinition : DockerFunctionAppDefinition<TestFunctionHost>
    {
        public override FunctionAppIdentifier Identifier => "auto-func";
    }

    private sealed class TestInfrastructureDefinition : DockerAzureInfrastructureDefinition
    {
        public override string? AzuriteImage => "custom/azurite:1";
        public override string? CosmosDbImage => "custom/cosmos:1";
        public override string? MsSqlImage => "custom/mssql:1";
        public override int? MsSqlMemoryLimitMb => 1024;
        public override string? ServiceBusImage => "custom/servicebus:1";
        public override string? MsSqlPassword => "StrongerPassword_123!";
        public override string? ServiceBusTopologyConfigPath => Path.Combine("Infrastructure", "bus-topology.json");
    }

    private sealed class ConflictingInfrastructureDefinition : DockerAzureInfrastructureDefinition
    {
        public override string? AzuriteImage => "custom/azurite:2";
    }

    private sealed class ConflictingTopologyServiceBusDefinition : DockerServiceBusDefinition
    {
        public override ServiceBusIdentifier Identifier => "bus";

        public override string TopologyConfigPath => Path.Combine("Conflicting", "bus-topology.json");
    }

    private sealed record TestCosmosItem(
        [property: JsonProperty("id")] string Id,
        [property: JsonProperty("PartitionKey")] string PartitionKey);

    private sealed record TestCosmosItemAlternatePartition(
        [property: JsonProperty("id")] string Id,
        [property: JsonProperty("TenantId")] string PartitionKey);

    private sealed class TestTableEntity : global::Azure.Data.Tables.ITableEntity
    {
        public string PartitionKey { get; set; } = "pk";
        public string RowKey { get; set; } = "rk";
        public DateTimeOffset? Timestamp { get; set; }
        public global::Azure.ETag ETag { get; set; }
    }

    private static ArtifactInstanceGeneric CreateArtifactInstance<TArtifactDescriber, TArtifactData, TArtifactReference>(
        TArtifactDescriber describer,
        ArtifactIdentifier identifier,
        TArtifactReference reference,
        TArtifactData data)
        where TArtifactDescriber : ArtifactDescriber<TArtifactDescriber, TArtifactData, TArtifactReference>, new()
        where TArtifactData : ArtifactData<TArtifactData, TArtifactDescriber, TArtifactReference>
        where TArtifactReference : ArtifactReference<TArtifactReference, TArtifactDescriber, TArtifactData>
    {
        return (ArtifactInstanceGeneric)Activator.CreateInstance(
            typeof(ArtifactInstance<TArtifactDescriber, TArtifactData, TArtifactReference>),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [describer, identifier, reference, data],
            culture: null)!;
    }

    private static void AssertInnerInvalidOperation(Action action)
    {
        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(action);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    private static ScopedLogger CreateLogger(IRunDebugger debugger)
    {
        Type debuggingRunSessionType = typeof(VariableStore).Assembly.GetType("TestFramework.Core.Debugger.DebuggingRunSession", throwOnError: true)!;
        object debuggingSession = Activator.CreateInstance(
            debuggingRunSessionType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [debugger],
            culture: null)!;

        return (ScopedLogger)typeof(ScopedLogger)
            .GetMethod("CreateWithDebuggerSession", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [debuggingSession])!;
    }

    private sealed class StubNetwork : DotNet.Testcontainers.Networks.INetwork
    {
        public string Id => "stub";
        public string Name => "stub";
        public Task CreateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingRunDebugger : IRunDebugger
    {
        public List<DebugLogEntry> LogEntries { get; } = [];

        public Task SignalInitTimelineRunAsync(string sessionId, string name, string projectPath, TimelineRunStructure runStructure) => Task.CompletedTask;
        public Task SignalEntityTransitionAsync(string sessionId, DebugEntityKind entityKind, string? stage, int? stepId, DebugLifecycleState state, DebugLifecycleState? previousState = null, DebugLifecycleState? outcomeState = null) => Task.CompletedTask;
        public Task SignalValueUpdateAsync(string sessionId, string name, DebugValueKind valueKind, string? stage, int? stepId, DebugValueEnvelope value) => Task.CompletedTask;
        public Task SignalLogEntryAsync(string sessionId, DebugLogEntry entry)
        {
            LogEntries.Add(entry);
            return Task.CompletedTask;
        }
        public Task SignalAssertionAsync(string sessionId, DebugAssertionEntry entry) => Task.CompletedTask;
        public Task SignalTimelineRunFinishedAsync(string sessionId) => Task.CompletedTask;
        public Task SignalAndWaitBreakpointHitAsync(string sessionId, string stage, int stepId) => Task.CompletedTask;
    }
}