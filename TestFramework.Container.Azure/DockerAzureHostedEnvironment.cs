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

namespace TestFramework.Container.Azure;

/// <summary>
/// Boots a configured Docker Azure environment once and reuses the hosted runtime state across multiple timeline runs.
/// </summary>
public sealed class DockerAzureHostedEnvironment : IAsyncDisposable
{
    private readonly PersistentEnvironmentContext<DockerAzurePersistentSetup> _persistentContext;
    private readonly ConfigInstance _persistentConfig;
    private readonly IServiceProvider _bootstrapServiceProvider;

    private DockerAzureHostedEnvironment(PersistentEnvironmentContext<DockerAzurePersistentSetup> persistentContext, ConfigInstance persistentConfig, IServiceProvider bootstrapServiceProvider)
    {
        _persistentContext = persistentContext;
        _persistentConfig = persistentConfig;
        _bootstrapServiceProvider = bootstrapServiceProvider;
    }

    /// <summary>
    /// Starts a hosted Docker Azure environment that can be reused across multiple runs.
    /// </summary>
    public static Task<DockerAzureHostedEnvironment> StartAsync(
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
        DockerAzurePersistentSetup setup = new(environment, persistentConfig, persistentRequirements, persistentSetupTimeout ?? TimeSpan.FromMinutes(2));
        PersistentEnvironmentContext<DockerAzurePersistentSetup> persistentContext = new(setup, bootstrapServiceProvider, disposePersistentServiceProvider: true);
        return Task.FromResult(new DockerAzureHostedEnvironment(persistentContext, persistentConfig, bootstrapServiceProvider));
    }

    /// <summary>
    /// Creates a run configuration layered on top of the persistent Docker Azure configuration snapshot.
    /// </summary>
    public ConfigInstance CreateRunConfig(Action<IConfigInstanceBuilder>? configure = null)
    {
        IConfigInstanceBuilder builder = _persistentConfig.SetupSubInstance();
        builder.AddService(services =>
        {
            services.AddSingleton(CloneStore(GetRequiredStore<StorageAccountConfig>()));
            services.AddSingleton(CloneStore(GetRequiredStore<CosmosContainerDbConfig>()));
            services.AddSingleton(CloneStore(GetRequiredStore<SqlDatabaseConfig>()));
            services.AddSingleton(CloneStore(GetRequiredStore<ServiceBusConfig>()));
            services.AddSingleton(CloneStore(GetRequiredStore<FunctionAppConfig>()));
            services.AddSingleton(CloneStore(GetRequiredStore<LogicAppConfig>()));
        });
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

    internal sealed class DockerAzurePersistentSetup : IConfigPersistentEnvironmentSetup
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
            => DockerAzurePersistentRootMapper.Map(_persistentRequirements);

        public TimeSpan GetPersistentSetupTimeout() => _persistentSetupTimeout;
    }

    private ConfigStore<TConfig> GetRequiredStore<TConfig>() where TConfig : class
    {
        return _bootstrapServiceProvider.GetRequiredService<ConfigStore<TConfig>>();
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