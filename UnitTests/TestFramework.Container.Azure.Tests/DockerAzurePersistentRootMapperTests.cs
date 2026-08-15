using TestFramework.Azure;
using TestFramework.Azure.Identifier;
using TestFramework.Container.Azure;
using TestFramework.Core.Environment;

namespace TestFramework.Container.Azure.Tests;

public class DockerAzurePersistentRootMapperTests
{
    [Fact]
    public void Map_StorageOnlyRequirement_OnlyKeepsNetworkAndAzurite()
    {
        IReadOnlyCollection<EnvComponentIdentifier> result = DockerAzurePersistentRootMapper.Map(
            new DockerAzureEnvironment(),
        [
            new(AzureEnvironmentResourceKinds.Storage, "PersistentStorage"),
        ]);

        Assert.Equal(2, result.Count);
        Assert.Contains(DockerAzureEnvironment.NetworkComponentId, result);
        Assert.Contains(DockerAzureEnvironment.AzuriteComponentId, result);
        Assert.DoesNotContain(DockerAzureEnvironment.CosmosDbComponentId, result);
        Assert.DoesNotContain(DockerAzureEnvironment.MsSqlComponentId, result);
        Assert.DoesNotContain(DockerAzureEnvironment.ServiceBusComponentId, result);
        Assert.DoesNotContain(DockerAzureEnvironment.FunctionAppComponentId, result);
    }

    [Fact]
    public void Map_MultipleRequirements_ReturnsExpectedPersistentRoots()
    {
        IReadOnlyCollection<EnvComponentIdentifier> result = DockerAzurePersistentRootMapper.Map(
            new DockerAzureEnvironment(),
        [
            new(AzureEnvironmentResourceKinds.Storage, "storage"),
            new(AzureEnvironmentResourceKinds.Cosmos, "cosmos"),
            new(AzureEnvironmentResourceKinds.Sql, "sql"),
            new(AzureEnvironmentResourceKinds.ServiceBus, "bus"),
            new(AzureEnvironmentResourceKinds.FunctionApp, "func"),
        ]);

        Assert.Equal(5, result.Count);
        Assert.Contains(DockerAzureEnvironment.NetworkComponentId, result);
        Assert.Contains(DockerAzureEnvironment.AzuriteComponentId, result);
        Assert.Contains(DockerAzureEnvironment.CosmosDbComponentId, result);
        Assert.Contains(DockerAzureEnvironment.MsSqlComponentId, result);
        Assert.Contains(DockerAzureEnvironment.ServiceBusComponentId, result);
        Assert.DoesNotContain(DockerAzureEnvironment.FunctionAppComponentId, result);
    }

    [Fact]
    public void Map_FunctionAppRequirement_OnlyKeepsTheEmulatorsItBindsTo()
    {
        DockerAzureEnvironment environment = DockerAzureEnvironment.ForFunctionAppWithStorage<StorageOnlyFunctionApp, StorageOnlyDefinition>(new FunctionAppIdentifier("storage-only-app"));

        IReadOnlyCollection<EnvComponentIdentifier> result = DockerAzurePersistentRootMapper.Map(
            environment,
        [
            new(AzureEnvironmentResourceKinds.FunctionApp, "storage-only-app"),
        ]);

        Assert.Equal(2, result.Count);
        Assert.Contains(DockerAzureEnvironment.NetworkComponentId, result);
        Assert.Contains(DockerAzureEnvironment.AzuriteComponentId, result);
        Assert.DoesNotContain(DockerAzureEnvironment.CosmosDbComponentId, result);
        Assert.DoesNotContain(DockerAzureEnvironment.ServiceBusComponentId, result);
        Assert.DoesNotContain(DockerAzureEnvironment.MsSqlComponentId, result);
    }

    private sealed class StorageOnlyFunctionApp
    {
    }

    private sealed class StorageOnlyDefinition : DockerStorageDefinition
    {
        public override StorageAccountIdentifier Identifier => new("storage-only-storage");
    }
}
