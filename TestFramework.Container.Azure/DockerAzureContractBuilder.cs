using System;
using System.Collections.Generic;
using TestFramework.Core.Environment;

namespace TestFramework.Container.Azure;

/// <summary>
/// Collects contracts exposed and required by a Docker Azure definition.
/// </summary>
public sealed class DockerAzureContractBuilder
{
    private readonly List<IEnvironmentResourceContract> _provides = [];
    private readonly List<IEnvironmentResourceContract> _requires = [];

    internal IReadOnlyCollection<IEnvironmentResourceContract> Provides => _provides;

    internal IReadOnlyCollection<IEnvironmentResourceContract> Requires => _requires;

    /// <summary>
    /// Declares a contract the definition offers to other definitions.
    /// </summary>
    /// <param name="contract">The provided contract.</param>
    /// <returns>This builder for chaining.</returns>
    public DockerAzureContractBuilder Provide(IEnvironmentResourceContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        _provides.Add(contract);
        return this;
    }

    /// <summary>
    /// Declares a contract the definition needs another definition to provide.
    /// </summary>
    /// <param name="contract">The required contract.</param>
    /// <returns>This builder for chaining.</returns>
    public DockerAzureContractBuilder Require(IEnvironmentResourceContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        _requires.Add(contract);
        return this;
    }
}