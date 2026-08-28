using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TestFramework.Config;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;

namespace TestFramework.Container.Azure;

/// <summary>
/// The persistent-stack setup behind every road into a hosted Docker Azure environment.
/// </summary>
/// <remarks>
/// One implementation for both <see cref="DockerAzureHostedCollectionFixture{TState}"/> and
/// <see cref="DockerAzureHostedEnvironment"/>: the two used to carry private copies of this type, and
/// the copies drifted on the setup timeout default - the fixture had learned that two minutes was too
/// low while the other road still defaulted to it (see
/// <see cref="DockerAzureDefaults.PersistentSetupTimeout"/>).
/// </remarks>
internal sealed class DockerAzurePersistentSetup : IConfigPersistentEnvironmentSetup
{
    private readonly DockerAzureEnvironment _environment;
    private readonly ConfigInstance _persistentConfig;
    private readonly IReadOnlyCollection<EnvironmentRequirement> _persistentRequirements;
    private readonly TimeSpan _persistentSetupTimeout;

    public DockerAzurePersistentSetup()
        : this(new DockerAzureEnvironment(), ConfigInstance.Create().LoadDockerAzureConfig().Build(), Array.Empty<EnvironmentRequirement>(), DockerAzureDefaults.PersistentSetupTimeout)
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

/// <summary>
/// Layers a run's own configuration services over a hosted environment's.
/// </summary>
internal sealed class HostedEnvironmentProvider(IEnvironmentProvider inner, IServiceProvider configServiceProvider) : IEnvironmentProviderProxy, IRunScopedServiceProviderFactory, IAsyncDisposable, IDisposable
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

/// <summary>
/// Answers from the run's own services first and the hosted configuration's second.
/// </summary>
internal sealed class FallbackServiceProvider(IServiceProvider primary, IServiceProvider fallback) : IServiceProvider
{
    public object? GetService(Type serviceType)
        => primary.GetService(serviceType) ?? fallback.GetService(serviceType);
}
