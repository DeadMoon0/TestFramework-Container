using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Config;
using TestFramework.Config.Builder.InstanceBuilder;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Azure;

/// <summary>
/// Describes the persistent container stack a <see cref="DockerAzureHostedCollectionFixture{TState}"/> boots.
/// </summary>
public interface IDockerAzureHostedFixtureState
{
    /// <summary>
    /// The requirements whose components are started once for the collection and kept alive across runs.
    /// </summary>
    IReadOnlyList<EnvironmentRequirement> PersistentRequirements { get; }

    /// <summary>
    /// How long the one-off bootstrap of the persistent slice may take.
    /// </summary>
    /// <remarks>
    /// The one default for every road into a hosted stack; the war story behind its size lives at
    /// <see cref="DockerAzureDefaults.PersistentSetupTimeout"/>.
    /// </remarks>
    TimeSpan PersistentSetupTimeout => DockerAzureDefaults.PersistentSetupTimeout;

    /// <summary>
    /// Creates the environment whose definitions the persistent stack and every run environment are built from.
    /// </summary>
    /// <returns>An environment with the collection's definitions included.</returns>
    DockerAzureEnvironment CreateEnvironment();

    /// <summary>
    /// Creates the configuration the persistent stack boots with; each run configuration is a sub-instance of it.
    /// </summary>
    /// <returns>The persistent configuration.</returns>
    ConfigInstance CreatePersistentConfig();
}

/// <summary>
/// Boots one persistent container stack for a whole test collection and hands out fresh run
/// environments on top of it.
/// </summary>
/// <typeparam name="TState">Describes the environment shape and the persistent configuration.</typeparam>
/// <remarks>
/// <para>
/// This deliberately does not implement <c>Xunit.IAsyncLifetime</c>. Doing so would make xunit a runtime
/// dependency of a library that is not a test project: a consumer's runtime would have to supply that
/// exact type, and xunit v3 moved it to <c>xunit.v3.core</c> with <c>ValueTask</c> returns, so a v3
/// consumer would meet a <c>TypeLoadException</c> rather than a build error.
/// </para>
/// <para>
/// <see cref="InitializeAsync"/> and <see cref="DisposeAsync"/> are still here with the signatures v2
/// expects, so adding the interface on your own fixture is a one-line change and the methods satisfy it
/// implicitly:
/// </para>
/// <code>
/// public sealed class MyFixture : DockerAzureHostedCollectionFixture&lt;MyState&gt;, IAsyncLifetime;
/// </code>
/// <para>
/// A xunit v3 consumer writes the adapter instead, which the hard binding used to make impossible:
/// </para>
/// <code>
/// public sealed class MyFixture : DockerAzureHostedCollectionFixture&lt;MyState&gt;, IAsyncLifetime
/// {
///     ValueTask IAsyncLifetime.InitializeAsync() =&gt; new(InitializeAsync());
///     ValueTask IAsyncDisposable.DisposeAsync() =&gt; new(DisposeAsync());
/// }
/// </code>
/// </remarks>
public class DockerAzureHostedCollectionFixture<TState>
    where TState : IDockerAzureHostedFixtureState, new()
{
    private readonly TState _state = new();
    private PersistentEnvironmentContext<DockerAzurePersistentSetup>? _persistentContext;
    private ConfigInstance? _persistentConfig;

    /// <summary>
    /// Boots the persistent container stack. Signature-compatible with <c>Xunit.IAsyncLifetime</c> v2.
    /// </summary>
    public async Task InitializeAsync()
    {
        ConfigInstance persistentConfig = _state.CreatePersistentConfig();
        IServiceProvider persistentServiceProvider = persistentConfig.BuildServiceProvider();
        DockerAzurePersistentSetup setup = new(_state.CreateEnvironment(), persistentConfig, _state.PersistentRequirements, _state.PersistentSetupTimeout);

        _persistentConfig = persistentConfig;

        // Awaited rather than constructed: bootstrapping starts the container stack, and doing that
        // in a constructor blocked the test collection's thread for the whole setup timeout.
        _persistentContext = await PersistentEnvironmentContext<DockerAzurePersistentSetup>
            .CreateAsync(setup, persistentServiceProvider, System.Threading.CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Tears the persistent container stack down. Signature-compatible with <c>Xunit.IAsyncLifetime</c> v2.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (_persistentContext is not null)
            await _persistentContext.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a fresh environment provider for one timeline run on top of the persistent stack.
    /// </summary>
    /// <remarks>Uses <c>null</c> for <c>configure</c>; a longer overload states it.</remarks>
    /// <returns>The run's environment provider.</returns>
    /// <exception cref="FrameworkStateException"><see cref="InitializeAsync"/> has not completed.</exception>
    public IEnvironmentProvider GetEnv()
        => this.GetEnv(null);

    /// <summary>
    /// Creates a fresh environment provider for one timeline run on top of the persistent stack, with a run
    /// configuration layered on the persistent configuration.
    /// </summary>
    /// <param name="configure">Adjusts the run configuration's builder, or <c>null</c> to take the persistent configuration as is.</param>
    /// <returns>The run's environment provider.</returns>
    /// <exception cref="FrameworkStateException"><see cref="InitializeAsync"/> has not completed.</exception>
    public IEnvironmentProvider GetEnv(Action<IConfigInstanceBuilder>? configure)
    {
        PersistentEnvironmentContext<DockerAzurePersistentSetup> persistentContext = _persistentContext ?? throw new FrameworkStateException("The hosted Docker Azure fixture has not finished initialization.");
        IServiceProvider configServiceProvider = CreateRunConfig(configure).BuildServiceProvider();
        return new HostedRunEnvironment(persistentContext.CreateEnvironment(), configServiceProvider);
    }

    /// <remarks>
    /// A sub-instance replays the persistent instance's registrations, so the resources that instance declared
    /// are already this run's. Nothing is copied across: a declaration is a fact, and a run reads it from the
    /// graph rather than from a container-held object that a previous run could have edited.
    /// </remarks>
    private ConfigInstance CreateRunConfig(Action<IConfigInstanceBuilder>? configure = null)
    {
        ConfigInstance persistentConfig = _persistentConfig ?? throw new FrameworkStateException("The hosted Docker Azure fixture has not finished initialization.");
        IConfigInstanceBuilder builder = persistentConfig.SetupSubInstance();
        configure?.Invoke(builder);
        return builder.Build();
    }

}
