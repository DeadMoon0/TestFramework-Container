using TestFramework.Azure.Identifier;
using TestFramework.Container.Azure;
using TestFramework.Container.Azure.Contracts;
using TestFramework.Core.Environment;
using TestFramework.Core.Steps;

namespace TestFramework.Container.Azure.Tests;

public class DockerAzureContractResolutionTests
{
    [Fact]
    public void ResolveComponents_ForBindsFunctionAppServiceBusDependencyFromContracts()
    {
        DockerAzureEnvironment environment = DockerAzureEnvironment.For<ContractFunctionAppDefinition>();
        Step<object?> functionStep = new TestFramework.Azure.Trigger.IsLive.IsLiveTrigger().FunctionApp("func");

        IReadOnlyCollection<EnvComponentIdentifier> result = environment.ResolveComponents([], ((IHasEnvironmentRequirements)functionStep).GetEnvironmentRequirements(null!));

        IReadOnlyCollection<ComponentContractBinding> bindings = (IReadOnlyCollection<ComponentContractBinding>)typeof(DockerAzureEnvironment)
            .GetMethod("GetContractBindings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(environment, [])!;

        Assert.Contains(DockerAzureEnvironment.FunctionAppComponentId, result);
        Assert.Contains(DockerAzureEnvironment.ServiceBusComponentId, result);
        Assert.Contains("bus", environment.UsedServiceBusIdentifiers);
        ComponentContractBinding binding = Assert.Single(bindings);
        Assert.Equal("functionapp:func", binding.ConsumerIdentity);
        Assert.Equal("servicebus:bus", binding.ProviderIdentity);
    }

    [Fact]
    public void ResolveComponents_ThrowsWhenExclusiveDependenciesResolveToSameIdentity()
    {
        DockerAzureEnvironment environment = DockerAzureEnvironment.For<ExclusiveFunctionAppDefinitionA>()
            .Include<ExclusiveFunctionAppDefinitionB>();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => environment.ResolveComponents([], []));

        Assert.Contains("exclusive", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("servicebus:bus", exception.Message, StringComparison.Ordinal);
    }

    private sealed class ContractServiceBusDefinition : DockerServiceBusDefinition
    {
        public override ServiceBusIdentifier Identifier => "bus";

        protected override void ConfigureContracts(DockerAzureContractBuilder contracts)
        {
            contracts.Provide(new ServiceBusEndpointContract(
                ContractKey: "reply",
                ServiceBusIdentifier: Identifier,
                EndpointKind: ServiceBusEndpointKind.Queue,
                EntityName: "processing-reply"));
        }
    }

    private sealed class ContractFunctionAppDefinition : DockerFunctionAppDefinition<DockerAzureContractResolutionTests>
    {
        public override FunctionAppIdentifier Identifier => "func";

        protected override void ConfigureDependencies(DockerAzureDependencyBuilder dependencies)
        {
            dependencies.Include<ContractServiceBusDefinition>();
        }

        protected override void ConfigureContracts(DockerAzureContractBuilder contracts)
        {
            contracts.Require(new ServiceBusEndpointContract(
                ContractKey: "reply",
                ServiceBusIdentifier: "bus",
                EndpointKind: ServiceBusEndpointKind.Queue,
                EntityName: "processing-reply"));
        }
    }

    private sealed class ExclusiveBusDefinition : DockerServiceBusDefinition
    {
        public override ServiceBusIdentifier Identifier => "bus";
    }

    private sealed class ExclusiveFunctionAppDefinitionA : DockerFunctionAppDefinition<DockerAzureContractResolutionTests>
    {
        public override FunctionAppIdentifier Identifier => "func-a";

        protected override void ConfigureDependencies(DockerAzureDependencyBuilder dependencies)
        {
            dependencies.Include<ExclusiveBusDefinition>(DependencyOwnership.Exclusive);
        }
    }

    private sealed class ExclusiveFunctionAppDefinitionB : DockerFunctionAppDefinition<DockerAzureContractResolutionTests>
    {
        public override FunctionAppIdentifier Identifier => "func-b";

        protected override void ConfigureDependencies(DockerAzureDependencyBuilder dependencies)
        {
            dependencies.Include<ExclusiveBusDefinition>(DependencyOwnership.Exclusive);
        }
    }
}
