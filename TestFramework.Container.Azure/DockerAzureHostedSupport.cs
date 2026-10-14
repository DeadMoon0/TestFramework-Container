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
