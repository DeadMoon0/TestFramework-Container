using TestFramework.Core.Steps;
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

    public override async Task<object?> CreateAsync(IEnvironmentProvider environment, RunContext context)
    {
        // Everything else in this environment depends on the network, so this is the first thing the
        // run does with Docker and the right place to make sure the client can reach it at all.
        ContainerRuntime.EnsureInitialized(context.Logger);

        INetwork network = await ContainerNetworkFactory.CreateAsync(DockerWebDefaults.NetworkNamePrefix, context.Deadline.Token).ConfigureAwait(false);
        GetWebEnvironment(environment).SetRuntimeState(Id, network);
        return network;
    }

    public override async Task DeconstructAsync(object? state, IEnvironmentProvider environment, RunContext context)
    {
        if (state is INetwork network)
            await ContainerDockerCommands.ForceRemoveNetworkAsync(network, context.Deadline.Token).ConfigureAwait(false);
        else if (state is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
    }
}
