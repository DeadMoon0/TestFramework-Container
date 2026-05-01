using TestFramework.Core.Environment;

namespace TestFramework.Container.Azure.Components;

internal abstract class DockerAzureEnvComponent : EnvComponent
{
    protected DockerAzureEnvironment GetDockerEnvironment(IEnvironmentProvider environment)
    {
        return environment as DockerAzureEnvironment
            ?? throw new InvalidOperationException($"Environment component '{Id}' requires {nameof(DockerAzureEnvironment)}.");
    }
}