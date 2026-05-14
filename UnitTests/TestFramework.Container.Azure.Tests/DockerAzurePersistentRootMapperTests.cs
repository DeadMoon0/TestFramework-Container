using TestFramework.Azure;
using TestFramework.Container.Azure;
using TestFramework.Core.Environment;

namespace TestFramework.Container.Azure.Tests;

public class DockerAzurePersistentRootMapperTests
{
    [Fact]
    public void Map_StorageOnlyRequirement_OnlyKeepsNetworkAndAzurite()
    {
        IReadOnlyCollection<EnvComponentIdentifier> result = DockerAzurePersistentRootMapper.Map(
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
        Assert.DoesNotContain(DockerAzureEnvironment.LogicAppComponentId, result);
    }

    [Fact]
    public void Map_MultipleRequirements_ReturnsExpectedPersistentRoots()
    {
        IReadOnlyCollection<EnvComponentIdentifier> result = DockerAzurePersistentRootMapper.Map(
        [
            new(AzureEnvironmentResourceKinds.Storage, "storage"),
            new(AzureEnvironmentResourceKinds.Cosmos, "cosmos"),
            new(AzureEnvironmentResourceKinds.Sql, "sql"),
            new(AzureEnvironmentResourceKinds.ServiceBus, "bus"),
            new(AzureEnvironmentResourceKinds.FunctionApp, "func"),
            new(AzureEnvironmentResourceKinds.LogicApp, "logic"),
        ]);

        Assert.Equal(5, result.Count);
        Assert.Contains(DockerAzureEnvironment.NetworkComponentId, result);
        Assert.Contains(DockerAzureEnvironment.AzuriteComponentId, result);
        Assert.Contains(DockerAzureEnvironment.CosmosDbComponentId, result);
        Assert.Contains(DockerAzureEnvironment.MsSqlComponentId, result);
        Assert.Contains(DockerAzureEnvironment.ServiceBusComponentId, result);
        Assert.DoesNotContain(DockerAzureEnvironment.FunctionAppComponentId, result);
        Assert.DoesNotContain(DockerAzureEnvironment.LogicAppComponentId, result);
    }
}