using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using System.Reflection;
using TestFramework.Azure.DB.CosmosDB;
using TestFramework.Azure.Configuration;
using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Azure.DB.SqlServer;
using TestFramework.Azure.Identifier;
using TestFramework.Azure.ServiceBus;
using TestFramework.Azure.StorageAccount.Blob;
using TestFramework.Azure.StorageAccount.Table;
using TestFramework.Azure.Trigger.IsLive;
using TestFramework.Container.Azure;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;
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
    public void ResolveComponents_IncludesForcedServiceBusComponent()
    {
        DockerAzureEnvironment environment = DockerAzureEnvironment.For<TestServiceBusDefinition>();

        IReadOnlyCollection<EnvComponentIdentifier> result = environment.ResolveComponents([], []);

        Assert.Contains(DockerAzureEnvironment.ServiceBusComponentId, result);
        Assert.Contains("bus", environment.UsedServiceBusIdentifiers);
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
        Step<object?> functionStep = new IsLiveTrigger().FunctionApp("func");
        Step<object?> blobStep = new IsLiveTrigger().Blob("storage");
        Step<object?> cosmosStep = new IsLiveTrigger().Cosmos("cosmos");
        Step<object?> sqlStep = new IsLiveTrigger().Sql("sql");

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
        Step<object?> functionStep = new IsLiveTrigger().FunctionApp("func");

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
    public void ResolveComponents_ForAppliesServiceBusTopologyPathFromDependencies()
    {
        DockerAzureEnvironment environment = DockerAzureEnvironment.For<TestFunctionAppDefinition>();

        IReadOnlyCollection<EnvComponentIdentifier> result = environment.ResolveComponents([], []);

        Assert.Contains(DockerAzureEnvironment.ServiceBusComponentId, result);
        Assert.Contains("bus", environment.UsedServiceBusIdentifiers);

        string topologyPath = (string)typeof(DockerAzureEnvironment)
            .GetMethod("GetServiceBusTopologyConfigPath", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(environment, [])!;
        Assert.Equal(Path.Combine("TestTopology", "servicebus.json"), topologyPath);
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
        Step<object?> functionStep = new IsLiveTrigger().FunctionApp("func");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => environment.ResolveComponents([], ((IHasEnvironmentRequirements)functionStep).GetEnvironmentRequirements(null!)));

        Assert.Contains("func", exception.Message);
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

        environment.ResolveComponents([], []);
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

        IReadOnlyCollection<EnvComponentIdentifier> result = environment.ResolveComponents([], []);

        Assert.Contains(DockerAzureEnvironment.ServiceBusComponentId, result);
        Assert.Equal("custom/azurite:1", typeof(DockerAzureEnvironment).GetMethod("GetAzuriteImage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(environment, []));
        Assert.Equal("custom/cosmos:1", typeof(DockerAzureEnvironment).GetMethod("GetCosmosDbImage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(environment, []));
        Assert.Equal("custom/mssql:1", typeof(DockerAzureEnvironment).GetMethod("GetMsSqlImage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(environment, []));
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

        Assert.Contains("Multiple Service Bus topology paths", exception.Message);
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

    private sealed class TestServiceBusDefinition : DockerServiceBusDefinition
    {
        public override ServiceBusIdentifier Identifier => "bus";

        public override string TopologyConfigPath => Path.Combine("TestTopology", "servicebus.json");
    }

    private sealed class TestDefaultServiceBusDefinition : DockerServiceBusDefinition
    {
        public override ServiceBusIdentifier Identifier => "bus";
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
                .UseServiceBusReply<TestServiceBusDefinition>();
        }
    }

    private sealed class TestInfrastructureDefinition : DockerAzureInfrastructureDefinition
    {
        public override string? AzuriteImage => "custom/azurite:1";
        public override string? CosmosDbImage => "custom/cosmos:1";
        public override string? MsSqlImage => "custom/mssql:1";
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

    private sealed class StubNetwork : DotNet.Testcontainers.Networks.INetwork
    {
        public string Id => "stub";
        public string Name => "stub";
        public Task CreateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}