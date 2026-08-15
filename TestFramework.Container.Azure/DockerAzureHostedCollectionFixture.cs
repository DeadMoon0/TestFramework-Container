using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TestFramework.Azure.Configuration;
using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Config;
using TestFramework.Config.Builder.InstanceBuilder;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Azure;

public interface IDockerAzureHostedFixtureState
{
    IReadOnlyList<EnvironmentRequirement> PersistentRequirements { get; }

    TimeSpan PersistentSetupTimeout => TimeSpan.FromMinutes(2);

    DockerAzureEnvironment CreateEnvironment();

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
    private IServiceProvider? _persistentServiceProvider;

    /// <summary>
    /// Boots the persistent container stack. Signature-compatible with <c>Xunit.IAsyncLifetime</c> v2.
    /// </summary>
    public async Task InitializeAsync()
    {
        ConfigInstance persistentConfig = _state.CreatePersistentConfig();
        IServiceProvider persistentServiceProvider = persistentConfig.BuildServiceProvider();
        DockerAzurePersistentSetup setup = new(_state.CreateEnvironment(), persistentConfig, _state.PersistentRequirements, _state.PersistentSetupTimeout);

        _persistentConfig = persistentConfig;
        _persistentServiceProvider = persistentServiceProvider;

        // Awaited rather than constructed: bootstrapping starts the container stack, and doing that
        // in a constructor blocked the test collection's thread for the whole setup timeout.
        _persistentContext = await PersistentEnvironmentContext<DockerAzurePersistentSetup>
            .CreateAsync(setup, persistentServiceProvider, disposePersistentServiceProvider: true)
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

    public IEnvironmentProvider GetEnv(Action<IConfigInstanceBuilder>? configure = null)
    {
        PersistentEnvironmentContext<DockerAzurePersistentSetup> persistentContext = _persistentContext ?? throw new FrameworkStateException("The hosted Docker Azure fixture has not finished initialization.");
        IServiceProvider configServiceProvider = CreateRunConfig(configure).BuildServiceProvider();
        return new HostedEnvironmentProvider(persistentContext.CreateEnvironment(), configServiceProvider);
    }

    private ConfigInstance CreateRunConfig(Action<IConfigInstanceBuilder>? configure = null)
    {
        ConfigInstance persistentConfig = _persistentConfig ?? throw new FrameworkStateException("The hosted Docker Azure fixture has not finished initialization.");
        IConfigInstanceBuilder builder = persistentConfig.SetupSubInstance();
        builder.AddService(services =>
        {
            services.AddSingleton(CloneStore(GetRequiredStore<StorageAccountConfig>()));
            services.AddSingleton(CloneStore(GetRequiredStore<CosmosContainerDbConfig>()));
            services.AddSingleton(CloneStore(GetRequiredStore<SqlDatabaseConfig>()));
            services.AddSingleton(CloneStore(GetRequiredStore<ServiceBusConfig>()));
            services.AddSingleton(CloneStore(GetRequiredStore<FunctionAppConfig>()));
        });
        configure?.Invoke(builder);
        return builder.Build();
    }

    private ConfigStore<TConfig> GetRequiredStore<TConfig>() where TConfig : class
    {
        IServiceProvider persistentServiceProvider = _persistentServiceProvider ?? throw new FrameworkStateException("The hosted Docker Azure fixture has not finished initialization.");
        return persistentServiceProvider.GetRequiredService<ConfigStore<TConfig>>();
    }

    private static ConfigStore<TConfig> CloneStore<TConfig>(ConfigStore<TConfig> source)
    {
        IReadOnlyDictionary<string, TConfig> snapshot = source.Snapshot();
        using IEnumerator<KeyValuePair<string, TConfig>> enumerator = snapshot.GetEnumerator();
        if (!enumerator.MoveNext())
            return new ConfigStore<TConfig>();

        ConfigStore<TConfig> store = ConfigStore<TConfig>.Create(enumerator.Current.Key, enumerator.Current.Value);
        while (enumerator.MoveNext())
            store.AddConfig(enumerator.Current.Key, enumerator.Current.Value);

        return store;
    }

    private sealed class DockerAzurePersistentSetup : IConfigPersistentEnvironmentSetup
    {
        private readonly DockerAzureEnvironment _environment;
        private readonly ConfigInstance _persistentConfig;
        private readonly IReadOnlyCollection<EnvironmentRequirement> _persistentRequirements;
        private readonly TimeSpan _persistentSetupTimeout;

        public DockerAzurePersistentSetup()
            : this(new DockerAzureEnvironment(), ConfigInstance.Create().LoadDockerAzureConfig().Build(), Array.Empty<EnvironmentRequirement>(), TimeSpan.FromMinutes(2))
        {
        }

        public DockerAzurePersistentSetup(DockerAzureEnvironment environment, ConfigInstance persistentConfig, IReadOnlyCollection<EnvironmentRequirement> persistentRequirements, TimeSpan persistentSetupTimeout)
        {
            _environment = environment.CloneDefinitions();
            _persistentConfig = persistentConfig;
            _persistentRequirements = persistentRequirements;
            _persistentSetupTimeout = persistentSetupTimeout;
        }

        public IEnvironmentProvider CreateEnvironment()
        {
            DockerAzureEnvironment environment = _environment.CloneDefinitions();
            environment.ResolveComponents(Array.Empty<ArtifactInstanceGeneric>(), _persistentRequirements);
            return environment;
        }

        public ConfigInstance CreatePersistentConfig() => _persistentConfig;

        public IReadOnlyCollection<EnvComponentIdentifier> GetPersistentComponentIdentifiers()
            => DockerAzurePersistentRootMapper.Map(_environment, _persistentRequirements);

        public TimeSpan GetPersistentSetupTimeout() => _persistentSetupTimeout;
    }

    private sealed class HostedEnvironmentProvider(IEnvironmentProvider inner, IServiceProvider configServiceProvider) : IEnvironmentProviderProxy, IRunScopedServiceProviderFactory, IAsyncDisposable, IDisposable
    {
        public IEnvironmentProvider InnerEnvironment => inner;

        public bool SupportsParallelComponentCreation => inner.SupportsParallelComponentCreation;

        public IReadOnlyCollection<EnvComponentIdentifier> ResolveComponents(IEnumerable<ArtifactInstanceGeneric> artifacts, IEnumerable<EnvironmentRequirement> requirements)
            => inner.ResolveComponents(artifacts, requirements);

        public EnvComponent GetComponent(EnvComponentIdentifier identifier)
            => inner.GetComponent(identifier);

        public IServiceProvider CreateRunScopedServiceProvider(IServiceProvider baseServiceProvider)
        {
            if (TryGetRunScopedFactory(inner, out IRunScopedServiceProviderFactory? factory))
                return factory!.CreateRunScopedServiceProvider(new FallbackServiceProvider(baseServiceProvider, configServiceProvider));

            return new FallbackServiceProvider(baseServiceProvider, configServiceProvider);
        }

        public void Dispose()
        {
            if (configServiceProvider is IDisposable disposable)
                disposable.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            if (configServiceProvider is IAsyncDisposable asyncDisposable)
                return asyncDisposable.DisposeAsync();

            if (configServiceProvider is IDisposable disposable)
                disposable.Dispose();

            return ValueTask.CompletedTask;
        }

        private static bool TryGetRunScopedFactory(IEnvironmentProvider environment, out IRunScopedServiceProviderFactory? factory)
        {
            if (environment is IRunScopedServiceProviderFactory directFactory)
            {
                factory = directFactory;
                return true;
            }

            if (environment is IEnvironmentProviderProxy proxy)
                return TryGetRunScopedFactory(proxy.InnerEnvironment, out factory);

            factory = null;
            return false;
        }
    }

    private sealed class FallbackServiceProvider(IServiceProvider primary, IServiceProvider fallback) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => primary.GetService(serviceType) ?? fallback.GetService(serviceType);
    }
}
