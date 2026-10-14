using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;

namespace TestFramework.Container;

/// <summary>
/// One run's environment on top of a hosted container stack: the stack's components, and the run's own
/// configuration over the stack's.
/// </summary>
/// <remarks>
/// Shared by every hosted road in the family. Container.Azure carried it privately, and a second package needing
/// the same layering would have carried a second copy - the shape that once let the Azure fixture and its sibling
/// road drift apart on a timeout default.
/// </remarks>
public sealed class HostedRunEnvironment : IEnvironmentProviderProxy, IRunScopedServiceProviderFactory, IAsyncDisposable, IDisposable
{
    private readonly IEnvironmentProvider _inner;
    private readonly IServiceProvider _configServiceProvider;

    /// <summary>
    /// Layers a run's configuration services over a hosted environment.
    /// </summary>
    /// <param name="inner">The environment the hosted stack created for this run.</param>
    /// <param name="configServiceProvider">The run's configuration services; this object disposes them.</param>
    public HostedRunEnvironment(IEnvironmentProvider inner, IServiceProvider configServiceProvider)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(configServiceProvider);

        _inner = inner;
        _configServiceProvider = configServiceProvider;
    }

    /// <inheritdoc />
    public IEnvironmentProvider InnerEnvironment => _inner;

    /// <inheritdoc />
    public bool SupportsParallelComponentCreation => _inner.SupportsParallelComponentCreation;

    /// <inheritdoc />
    public IReadOnlyCollection<EnvComponentIdentifier> ResolveComponents(IEnumerable<ArtifactInstanceGeneric> artifacts, IEnumerable<EnvironmentRequirement> requirements)
        => _inner.ResolveComponents(artifacts, requirements);

    /// <inheritdoc />
    public EnvComponent GetComponent(EnvComponentIdentifier identifier)
        => _inner.GetComponent(identifier);

    /// <summary>
    /// Answers from the run's own services first and the hosted configuration's second, through the inner
    /// environment's own run-scoped layer when it has one.
    /// </summary>
    /// <param name="baseServiceProvider">The run's own services.</param>
    /// <returns>The services the run resolves from.</returns>
    public IServiceProvider CreateRunScopedServiceProvider(IServiceProvider baseServiceProvider)
    {
        FallbackServiceProvider layered = new(baseServiceProvider, _configServiceProvider);

        return TryGetRunScopedFactory(_inner, out IRunScopedServiceProviderFactory? factory)
            ? factory!.CreateRunScopedServiceProvider(layered)
            : layered;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_configServiceProvider is IDisposable disposable)
            disposable.Dispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_configServiceProvider is IAsyncDisposable asyncDisposable)
            return asyncDisposable.DisposeAsync();

        Dispose();
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

    private sealed class FallbackServiceProvider(IServiceProvider primary, IServiceProvider fallback) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => primary.GetService(serviceType) ?? fallback.GetService(serviceType);
    }
}
