using System.Collections.Generic;
using TestFramework.Core.Environment;

namespace TestFramework.Container.Azure;

/// <summary>
/// Base of every definition a <see cref="DockerAzureEnvironment"/> includes: one containerised resource or
/// infrastructure setting, plus the definitions and contracts it depends on.
/// </summary>
public abstract class DockerAzureDefinition
{
    /// <summary>
    /// Declares the definitions this one depends on. They are included with it and started whenever it is used.
    /// The default declares none.
    /// </summary>
    /// <param name="dependencies">Collects the dependencies.</param>
    protected virtual void ConfigureDependencies(DockerAzureDependencyBuilder dependencies)
    {
    }

    /// <summary>
    /// Declares the resource contracts this definition provides and requires. Each requirement is bound to a
    /// matching provider (see <see cref="DockerAzureContractMatcher"/>) when the environment resolves, and the
    /// provider is started with the definition that requires it. The default declares none.
    /// </summary>
    /// <param name="contracts">Collects the contracts.</param>
    protected virtual void ConfigureContracts(DockerAzureContractBuilder contracts)
    {
    }

    /// <summary>
    /// Runs <see cref="ConfigureDependencies"/> on a fresh builder and returns what it declared.
    /// </summary>
    /// <returns>The declared dependencies, one per definition type.</returns>
    protected internal IReadOnlyCollection<ComponentDependency> GetDependencies()
    {
        DockerAzureDependencyBuilder dependencies = new();
        ConfigureDependencies(dependencies);
        return dependencies.Dependencies;
    }

    /// <summary>
    /// Runs <see cref="ConfigureContracts"/> on a fresh builder and returns the contracts it provides.
    /// </summary>
    /// <returns>The provided contracts.</returns>
    protected internal IReadOnlyCollection<IEnvironmentResourceContract> GetProvidedContracts()
    {
        DockerAzureContractBuilder contracts = new();
        ConfigureContracts(contracts);
        return contracts.Provides;
    }

    /// <summary>
    /// Runs <see cref="ConfigureContracts"/> on a fresh builder and returns the contracts it requires.
    /// </summary>
    /// <returns>The required contracts.</returns>
    protected internal IReadOnlyCollection<IEnvironmentResourceContract> GetRequiredContracts()
    {
        DockerAzureContractBuilder contracts = new();
        ConfigureContracts(contracts);
        return contracts.Requires;
    }
}
