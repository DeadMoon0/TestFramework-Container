using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;
using TestFramework.Core.Logging;
using TestFramework.Core.Variables;

namespace TestFramework.Container.Azure.Components;

internal sealed class DockerNetworkEnvComponent : EnvComponent
{
    public override EnvComponentIdentifier Id => DockerAzureEnvironment.NetworkComponentId;

    public override async Task<object?> CreateAsync(IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        INetwork network = new NetworkBuilder()
            .WithName($"testframework-{Guid.NewGuid():N}")
            .Build();

        await network.CreateAsync(cancellationToken)
            .ConfigureAwait(false);

        if (environment is DockerAzureEnvironment dockerEnvironment)
            dockerEnvironment.SetRuntimeState(Id, network);

        return network;
    }

    public override async Task DeconstructAsync(object? state, IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        if (state is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else if (state is IDisposable disposable)
            disposable.Dispose();
    }
}