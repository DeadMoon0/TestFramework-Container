using TestFramework.Azure.Identifier;
using TestFramework.Container.Azure;
using TestFramework.Container.Azure.Contracts;
using TestFramework.Core.Environment;

namespace TestFramework.Container.Azure.Tests;

public class DockerAzureDefinitionContractsTests
{
    [Fact]
    public void DockerAzureDefinition_ExposesDependencyOwnershipMetadata()
    {
        TestDependencyOwnerDefinition definition = new();

        IReadOnlyCollection<ComponentDependency> dependencies = definition.GetDependenciesForTest();

        ComponentDependency shared = Assert.Single(dependencies.Where(x => x.ComponentType == typeof(TestStorageDefinition)));
        Assert.Equal(DependencyOwnership.Shared, shared.Ownership);

        ComponentDependency exclusive = Assert.Single(dependencies.Where(x => x.ComponentType == typeof(TestServiceBusDefinition)));
        Assert.Equal(DependencyOwnership.Exclusive, exclusive.Ownership);
    }

    [Fact]
    public void DockerAzureDefinition_ExposesProvidedAndRequiredContracts()
    {
        TestContractDefinition definition = new();

        BlobContainerContract provided = Assert.IsType<BlobContainerContract>(Assert.Single(definition.GetProvidedContractsForTest()));
        ServiceBusEndpointContract required = Assert.IsType<ServiceBusEndpointContract>(Assert.Single(definition.GetRequiredContractsForTest()));

        Assert.Equal("uploads", provided.ContractKey);
        Assert.Equal(new StorageAccountIdentifier("storage"), provided.StorageIdentifier);
        Assert.Equal("trigger", required.ContractKey);
        Assert.Equal(new ServiceBusIdentifier("bus"), required.ServiceBusIdentifier);
    }

    [Fact]
    public void Include_UsesResolvedDependencyMetadataWithoutReplayingConfigureDependencies()
    {
        CountingDependencyOwnerDefinition definition = new();
        DockerAzureEnvironment environment = new();

        environment.Include(definition);

        Assert.Equal(1, definition.ConfigureDependenciesCallCount);
    }

    [Fact]
    public void DockerAzureContractMatcher_UsesIdentityAndOptionalNarrowingFields()
    {
        BlobContainerContract requiredBlob = new(
            ContractKey: "uploads",
            StorageIdentifier: new StorageAccountIdentifier("storage"),
            ContainerName: "uploads",
            BindingName: null,
            AccessMode: BlobAccessMode.Read);
        BlobContainerContract providedBlob = new(
            ContractKey: "uploads",
            StorageIdentifier: new StorageAccountIdentifier("storage"),
            ContainerName: "uploads",
            BindingName: "AzureWebJobsStorage",
            AccessMode: BlobAccessMode.ReadWrite);
        BlobContainerContract wrongStorageBlob = providedBlob with { StorageIdentifier = new StorageAccountIdentifier("other") };

        Assert.True(DockerAzureContractMatcher.IsMatch(requiredBlob, providedBlob));
        Assert.False(DockerAzureContractMatcher.IsMatch(requiredBlob, wrongStorageBlob));

        ServiceBusEndpointContract requiredSubscription = new(
            ContractKey: "trigger",
            ServiceBusIdentifier: new ServiceBusIdentifier("bus"),
            EndpointKind: ServiceBusEndpointKind.TopicSubscription,
            EntityName: "orders",
            SubscriptionName: "processor",
            BindingName: null);
        ServiceBusEndpointContract providedSubscription = requiredSubscription with { BindingName = "AzureWebJobsServiceBus" };
        ServiceBusEndpointContract wrongSubscription = providedSubscription with { SubscriptionName = "other" };

        Assert.True(DockerAzureContractMatcher.IsMatch(requiredSubscription, providedSubscription));
        Assert.False(DockerAzureContractMatcher.IsMatch(requiredSubscription, wrongSubscription));
    }

    private sealed class TestDependencyOwnerDefinition : DockerAzureDefinition
    {
        protected override void ConfigureDependencies(DockerAzureDependencyBuilder dependencies)
        {
            dependencies.Include<TestStorageDefinition>();
            dependencies.Include<TestServiceBusDefinition>(DependencyOwnership.Exclusive);
        }

        public IReadOnlyCollection<ComponentDependency> GetDependenciesForTest() => GetDependencies();
    }

    private sealed class TestContractDefinition : DockerAzureDefinition
    {
        protected override void ConfigureContracts(DockerAzureContractBuilder contracts)
        {
            contracts.Provide(new BlobContainerContract(
                ContractKey: "uploads",
                StorageIdentifier: new StorageAccountIdentifier("storage"),
                ContainerName: "uploads",
                BindingName: "AzureWebJobsStorage"));

            contracts.Require(new ServiceBusEndpointContract(
                ContractKey: "trigger",
                ServiceBusIdentifier: new ServiceBusIdentifier("bus"),
                EndpointKind: ServiceBusEndpointKind.Queue,
                EntityName: "sample-submission"));
        }

        public IReadOnlyCollection<IEnvironmentResourceContract> GetProvidedContractsForTest() => GetProvidedContracts();

        public IReadOnlyCollection<IEnvironmentResourceContract> GetRequiredContractsForTest() => GetRequiredContracts();
    }

    private sealed class CountingDependencyOwnerDefinition : DockerAzureInfrastructureDefinition
    {
        public int ConfigureDependenciesCallCount { get; private set; }

        protected override void ConfigureDependencies(DockerAzureDependencyBuilder dependencies)
        {
            ConfigureDependenciesCallCount++;
            dependencies.Include<TestStorageDefinition>();
        }
    }

    private sealed class TestStorageDefinition : DockerStorageDefinition
    {
        public override StorageAccountIdentifier Identifier => new("storage");
    }

    private sealed class TestServiceBusDefinition : DockerServiceBusDefinition
    {
        public override ServiceBusIdentifier Identifier => new("bus");
    }
}
