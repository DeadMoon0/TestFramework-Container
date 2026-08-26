using TestFramework.Core.Steps;
using DotNet.Testcontainers.Networks;
using System;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;
using TestFramework.Core.Logging;
using TestFramework.Core.Variables;

namespace TestFramework.Container.Azure.Components;

internal sealed class DockerNetworkEnvComponent : DockerAzureEnvComponent
{
    public override EnvComponentIdentifier Id => DockerAzureEnvironment.NetworkComponentId;

    public override EnvComponentReuseMode ReuseMode => EnvComponentReuseMode.PersistentContext;

    public override async Task<object?> CreateAsync(IEnvironmentProvider environment, RunContext context)
    {
        // Everything else in this environment depends on the network, so this is the first thing the
        // run does with Docker and the right place to make sure the client can reach it at all.
        ContainerRuntime.EnsureInitialized(context.Logger);

        if (environment is DockerAzureEnvironment dockerEnvironment)
            dockerEnvironment.LogPendingResolutionSummary(context.Logger);

        INetwork network = await ContainerNetworkFactory.CreateAsync("testframework", context.Deadline.Token).ConfigureAwait(false);

        if (environment is DockerAzureEnvironment runtimeEnvironment)
            runtimeEnvironment.SetRuntimeState(Id, network);

        return network;
    }

    public override async Task DeconstructAsync(object? state, IEnvironmentProvider environment, RunContext context)
    {
        if (state is INetwork network)
        {
            await ForceRemoveNetworkAsync(network, context.Deadline.Token).ConfigureAwait(false);
        }
        else if (state is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (state is IDisposable disposable)
            disposable.Dispose();
    }
}