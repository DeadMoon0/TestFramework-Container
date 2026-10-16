using System;
using System.Collections.Generic;
using TestFramework.Core.Environment;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Azure;

/// <summary>
/// Collects the definitions a <see cref="DockerAzureDefinition"/> depends on.
/// </summary>
public sealed class DockerAzureDependencyBuilder
{
    private readonly Dictionary<Type, ComponentDependency> _dependencies = [];

    internal IReadOnlyCollection<ComponentDependency> Dependencies => _dependencies.Values;

    /// <summary>
    /// Declares a shared dependency on <typeparamref name="TDefinition"/>.
    /// </summary>
    /// <remarks>Uses <see cref="DependencyOwnership.Shared"/> for <c>ownership</c>; a longer overload states it.</remarks>
    /// <typeparam name="TDefinition">The definition depended on.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public DockerAzureDependencyBuilder Include<TDefinition>()
        where TDefinition : DockerAzureDefinition, new()
        => this.Include<TDefinition>(DependencyOwnership.Shared);

    /// <summary>
    /// Declares a dependency on <typeparamref name="TDefinition"/>. Declaring the same type again is allowed only
    /// with the same <paramref name="ownership"/>.
    /// </summary>
    /// <typeparam name="TDefinition">The definition depended on.</typeparam>
    /// <param name="ownership">Whether the dependency may be shared with other dependents.</param>
    /// <returns>This builder for chaining.</returns>
    /// <exception cref="FrameworkConfigurationException">The type was already declared with a different ownership.</exception>
    public DockerAzureDependencyBuilder Include<TDefinition>(DependencyOwnership ownership)
        where TDefinition : DockerAzureDefinition, new()
    {
        Type definitionType = typeof(TDefinition);
        ComponentDependency dependency = new() { ComponentType = definitionType, Ownership = ownership };

        if (_dependencies.TryGetValue(definitionType, out ComponentDependency? existing) && existing is not null && existing.Ownership != ownership)
            throw new FrameworkConfigurationException($"Dependency '{definitionType.FullName}' was configured with conflicting ownership values.");

        _dependencies[definitionType] = dependency;
        return this;
    }
}
