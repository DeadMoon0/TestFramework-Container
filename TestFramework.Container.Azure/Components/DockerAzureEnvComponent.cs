using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using System;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Core.Exceptions;
using TestFramework.Core.Environment;

namespace TestFramework.Container.Azure.Components;

internal abstract class DockerAzureEnvComponent : EnvComponent
{
    protected DockerAzureEnvironment GetDockerEnvironment(IEnvironmentProvider environment)
    {
        if (environment is DockerAzureEnvironment dockerEnvironment)
            return dockerEnvironment;

        if (environment is IEnvironmentProviderProxy proxy)
            return GetDockerEnvironment(proxy.InnerEnvironment);

        throw new FrameworkStateException($"Environment component '{Id}' requires {nameof(DockerAzureEnvironment)}.");
    }

    protected static Task ForceRemoveContainerAsync(IContainer container, CancellationToken cancellationToken)
        => ContainerDockerCommands.ForceRemoveContainerAsync(container, cancellationToken);

    protected static Task ForceRemoveNetworkAsync(INetwork network, CancellationToken cancellationToken)
        => ContainerDockerCommands.ForceRemoveNetworkAsync(network, cancellationToken);
}
