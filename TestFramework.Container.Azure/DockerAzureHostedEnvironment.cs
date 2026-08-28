using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Config;
using TestFramework.Config.Builder.InstanceBuilder;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;

namespace TestFramework.Container.Azure;

/// <summary>
/// Boots a configured Docker Azure environment once and reuses the hosted runtime state across multiple timeline runs.
/// </summary>
public sealed class DockerAzureHostedEnvironment : IAsyncDisposable
{
    private readonly PersistentEnvironmentContext<DockerAzurePersistentSetup> _persistentContext;
    private readonly ConfigInstance _persistentConfig;

    private DockerAzureHostedEnvironment(PersistentEnvironmentContext<DockerAzurePersistentSetup> persistentContext, ConfigInstance persistentConfig)
    {
        _persistentContext = persistentContext;
        _persistentConfig = persistentConfig;
    }

    /// <summary>
    /// Starts a hosted Docker Azure environment that can be reused across multiple runs.
    /// </summary>
    public static async Task<DockerAzureHostedEnvironment> StartAsync(
        DockerAzureEnvironment environment,
        ConfigInstance persistentConfig,
        IReadOnlyCollection<EnvironmentRequirement> persistentRequirements,
        TimeSpan? persistentSetupTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(persistentConfig);
        ArgumentNullException.ThrowIfNull(persistentRequirements);

        cancellationToken.ThrowIfCancellationRequested();

        IServiceProvider bootstrapServiceProvider = persistentConfig.BuildServiceProvider();
        DockerAzurePersistentSetup setup = new(environment, persistentConfig, persistentRequirements, persistentSetupTimeout ?? DockerAzureDefaults.PersistentSetupTimeout);

        // Awaited rather than constructed: bootstrapping starts containers, so doing it in a
        // constructor blocked the caller for the whole setup timeout and deadlocked under a
        // synchronization context. This also makes the cancellation token mean something.
        PersistentEnvironmentContext<DockerAzurePersistentSetup> persistentContext =
            await PersistentEnvironmentContext<DockerAzurePersistentSetup>
                .CreateAsync(setup, bootstrapServiceProvider, disposePersistentServiceProvider: true, cancellationToken)
                .ConfigureAwait(false);

        return new DockerAzureHostedEnvironment(persistentContext, persistentConfig);
    }

    /// <summary>
    /// Creates a run configuration layered on top of the persistent Docker Azure configuration snapshot.
    /// </summary>
    /// <remarks>
    /// A sub-instance replays the persistent instance's registrations, so the resources that instance declared
    /// are already this run's. Nothing is copied across: a declaration is a fact, and a run reads it from the
    /// graph rather than from a container-held object that a previous run could have edited.
    /// </remarks>
    public ConfigInstance CreateRunConfig(Action<IConfigInstanceBuilder>? configure = null)
    {
        IConfigInstanceBuilder builder = _persistentConfig.SetupSubInstance();
        configure?.Invoke(builder);
        return builder.Build();
    }

    /// <summary>
    /// Creates a fresh environment provider for a single timeline run while reusing the hosted Docker runtime state.
    /// </summary>
    public IEnvironmentProvider CreateEnvironment(Action<IConfigInstanceBuilder>? configure = null)
    {
        IServiceProvider configServiceProvider = CreateRunConfig(configure).BuildServiceProvider();
        return new HostedEnvironmentProvider(_persistentContext.CreateEnvironment(), configServiceProvider);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return _persistentContext.DisposeAsync();
    }

}