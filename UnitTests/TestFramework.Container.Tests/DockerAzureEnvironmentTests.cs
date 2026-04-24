using TestFramework.Container.AzureDocker;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;
using TestFramework.Azure.DB.CosmosDB;
using TestFramework.Azure.DB.SqlServer;
using TestFramework.Azure.FunctionApp;
using TestFramework.Azure.Identifier;
using TestFramework.Azure.ServiceBus;
using TestFramework.Azure.Trigger.IsLive;
using TestFramework.Azure.StorageAccount.Blob;
using TestFramework.Azure.StorageAccount.Table;
using TestFramework.Core.Logging;
using TestFramework.Core.Steps;
using TestFramework.Core.Variables;
using System.Reflection;
using Azure.Messaging.ServiceBus;
using Newtonsoft.Json;

namespace TestFramework.Container.Tests;

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
        DockerAzureEnvironment environment = new(new DockerAzureEnvironmentOptions
        {
            RequiredServiceBusIdentifiers = [new ServiceBusIdentifier("bus")],
        });

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
        DockerAzureEnvironment environment = new();
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
        string relativePath = Path.Combine("AzureDocker", "Configurations", "ServiceBus", "config.json");

        string resolved = typeof(DockerAzureEnvironment).Assembly
            .GetType("TestFramework.Container.AzureDocker.ServiceBusConfigLocator")!
            .GetMethod("Resolve", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [relativePath])!
            .ToString()!;

        Assert.True(File.Exists(resolved));
    }

    private static void InvokeEnsureAzurite(string connectionString)
        => typeof(DockerAzureEnvironment).Assembly
            .GetType("TestFramework.Container.AzureDocker.ConnectionStringGuards")!
            .GetMethod("EnsureAzurite", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [connectionString]);

    private static void InvokeEnsureServiceBus(string connectionString)
        => typeof(DockerAzureEnvironment).Assembly
            .GetType("TestFramework.Container.AzureDocker.ConnectionStringGuards")!
            .GetMethod("EnsureServiceBus", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [connectionString]);

    private static void InvokeEnsureCosmos(string connectionString)
        => typeof(DockerAzureEnvironment).Assembly
            .GetType("TestFramework.Container.AzureDocker.ConnectionStringGuards")!
            .GetMethod("EnsureCosmos", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [connectionString]);

    private static void InvokeEnsureSql(string connectionString)
        => typeof(DockerAzureEnvironment).Assembly
            .GetType("TestFramework.Container.AzureDocker.ConnectionStringGuards")!
            .GetMethod("EnsureSql", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [connectionString]);

    private sealed class TestRow;

    private sealed record TestCosmosItem(
        [property: JsonProperty("id")] string Id,
        [property: JsonProperty("PartitionKey")] string PartitionKey);

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
}