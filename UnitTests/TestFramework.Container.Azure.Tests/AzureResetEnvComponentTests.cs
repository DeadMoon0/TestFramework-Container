using TestFramework.Azure;
using TestFramework.Azure.Identifier;
using TestFramework.Container.Azure;
using TestFramework.Core.Environment;

namespace TestFramework.Container.Azure.Tests;

/// <summary>
/// Covers how the per-run purge is wired into the graph, which is where it can go wrong silently.
/// </summary>
public class AzureResetEnvComponentTests
{
    [Fact]
    public void ResolveComponents_WithNoDeclaredResources_DoesNotResolveTheReset()
    {
        DockerAzureEnvironment environment = new();

        IReadOnlyCollection<EnvComponentIdentifier> result = environment.ResolveComponents([], []);

        Assert.DoesNotContain(DockerAzureEnvironment.AzureResetComponentId, result);
    }

    [Fact]
    public void ResolveComponents_WithADeclaredResource_ResolvesTheReset()
    {
        DockerAzureEnvironment environment = new();

        IReadOnlyCollection<EnvComponentIdentifier> result = environment.ResolveComponents(
            [],
            [new(AzureEnvironmentResourceKinds.Storage, "storage")]);

        Assert.Contains(DockerAzureEnvironment.AzureResetComponentId, result);
    }

    [Fact]
    public void ResetComponent_IsPerRunAndDependsOnlyOnTheEmulatorsThatResolved()
    {
        DockerAzureEnvironment environment = new();
        environment.ResolveComponents([], [new(AzureEnvironmentResourceKinds.Storage, "storage")]);

        EnvComponent reset = environment.GetComponent(DockerAzureEnvironment.AzureResetComponentId);

        // Persistent components may not depend on a per-run one, so the reset has to be the per-run
        // half of the pair and depend downwards.
        Assert.Equal(EnvComponentReuseMode.PerRun, reset.ReuseMode);
        Assert.Contains(DockerAzureEnvironment.NetworkComponentId, reset.Dependencies);
        Assert.Contains(DockerAzureEnvironment.AzuriteComponentId, reset.Dependencies);
        Assert.DoesNotContain(DockerAzureEnvironment.CosmosDbComponentId, reset.Dependencies);
        Assert.DoesNotContain(DockerAzureEnvironment.ServiceBusComponentId, reset.Dependencies);
        Assert.DoesNotContain(DockerAzureEnvironment.MsSqlComponentId, reset.Dependencies);
    }

    [Fact]
    public void FunctionAppComponent_DependsOnTheResetSoItNeverStartsAgainstUnpurgedStores()
    {
        DockerAzureEnvironment environment = DockerAzureEnvironment.ForFunctionAppWithStorage<ResetProbeFunctionApp, ResetProbeStorage>(new FunctionAppIdentifier("reset-probe-app"));
        environment.ResolveComponents([], [new(AzureEnvironmentResourceKinds.FunctionApp, "reset-probe-app")]);

        EnvComponent functionApp = environment.GetComponent(DockerAzureEnvironment.FunctionAppComponentId);

        Assert.Contains(DockerAzureEnvironment.AzureResetComponentId, functionApp.Dependencies);
    }

    [Fact]
    public void PersistentRoots_NeverIncludeTheReset()
    {
        // A per-run component named as a persistent root makes ValidatePersistentRoots throw.
        IReadOnlyCollection<EnvComponentIdentifier> roots = DockerAzurePersistentRootMapper.Map(
            new DockerAzureEnvironment(),
            [
                new(AzureEnvironmentResourceKinds.Storage, "storage"),
                new(AzureEnvironmentResourceKinds.ServiceBus, "bus"),
            ]);

        Assert.DoesNotContain(DockerAzureEnvironment.AzureResetComponentId, roots);
    }

    [Fact]
    public void UseResetMode_IsCarriedIntoAClone()
    {
        DockerAzureEnvironment environment = new DockerAzureEnvironment().UseResetMode(AzureResetMode.None);

        DockerAzureEnvironment clone = (DockerAzureEnvironment)typeof(DockerAzureEnvironment)
            .GetMethod("CloneDefinitions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(environment, [])!;

        Assert.Equal(AzureResetMode.None, GetResetMode(clone));
    }

    [Fact]
    public void ResetMode_DefaultsToPurging()
    {
        Assert.Equal(AzureResetMode.PurgeDeclaredResources, GetResetMode(new DockerAzureEnvironment()));
    }

    private static AzureResetMode GetResetMode(DockerAzureEnvironment environment)
        => (AzureResetMode)typeof(DockerAzureEnvironment)
            .GetMethod("GetResetMode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(environment, [])!;

    private sealed class ResetProbeFunctionApp
    {
    }

    private sealed class ResetProbeStorage : DockerStorageDefinition
    {
        public override StorageAccountIdentifier Identifier => new("reset-probe-storage");
    }
}
