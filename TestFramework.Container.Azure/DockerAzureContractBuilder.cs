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

    public DockerAzureContractBuilder Provide(IEnvironmentResourceContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        _provides.Add(contract);
        return this;
    }

    public DockerAzureContractBuilder Require(IEnvironmentResourceContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        _requires.Add(contract);
        return this;
    }
}