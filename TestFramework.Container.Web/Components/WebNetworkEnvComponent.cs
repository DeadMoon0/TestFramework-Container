using System;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Testcontainers.Networks;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;
using TestFramework.Core.Logging;
using TestFramework.Core.Variables;

namespace TestFramework.Container.Web.Components;

/// <summary>
/// Creates the Docker network the environment's containers share.
/// </summary>
internal sealed class WebNetworkEnvComponent : WebEnvComponentBase
{
    public override EnvComponentIdentifier Id => DockerWebEnvironment.NetworkComponentId;

    public override EnvComponentReuseMode ReuseMode => EnvComponentReuseMode.PersistentContext;

    public override async Task<object?> CreateAsync(IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        INetwork network = await ContainerNetworkFactory.CreateAsync(DockerWebDefaults.NetworkNamePrefix, cancellationToken).ConfigureAwait(false);
        GetWebEnvironment(environment).SetRuntimeState(Id, network);
        return network;
    }

    public override async Task DeconstructAsync(object? state, IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        if (state is INetwork network)
            await ContainerDockerCommands.ForceRemoveNetworkAsync(network, cancellationToken).ConfigureAwait(false);
        else if (state is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
    }
}
