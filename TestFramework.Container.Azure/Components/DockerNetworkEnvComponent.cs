using DotNet.Testcontainers.Builders;
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

    public override async Task<object?> CreateAsync(IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        if (environment is DockerAzureEnvironment dockerEnvironment)
            dockerEnvironment.LogPendingResolutionSummary(logger);

        INetwork network = new NetworkBuilder()
            .WithName($"testframework-{Guid.NewGuid():N}")
            .Build();

        await network.CreateAsync(cancellationToken)
            .ConfigureAwait(false);

        if (environment is DockerAzureEnvironment runtimeEnvironment)
            runtimeEnvironment.SetRuntimeState(Id, network);

        return network;
    }

    public override async Task DeconstructAsync(object? state, IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        if (state is INetwork network)
        {
            await ForceRemoveNetworkAsync(network, cancellationToken).ConfigureAwait(false);
        }
        else if (state is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (state is IDisposable disposable)
            disposable.Dispose();
    }
}