#region snippet:hosted-fixture
using TestFramework.Azure;
using TestFramework.Azure.Extensions;
using TestFramework.Config;
using TestFramework.Container.Azure;
using TestFramework.Core.Environment;
using TestFramework.Core.Timelines;
using Xunit;
#endregion
using System.Collections.Generic;
using System.Threading.Tasks;
using TestFramework.Container.Azure.Readme.Composition;
using TestFramework.Container.Azure.Readme.Infrastructure;

namespace TestFramework.Container.Azure.Readme.Hosted;

#region snippet:hosted-fixture
[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class DockerAzureHostedCollectionDefinition : ICollectionFixture<DockerAzureHostedFixture>
{
    public const string CollectionName = "DockerAzureHosted";
}

public sealed class DockerAzureHostedFixtureState : IDockerAzureHostedFixtureState
{
    public IReadOnlyList<EnvironmentRequirement> PersistentRequirements =>
    [
        new(AzureResourceKinds.Storage, "MainStorage"),
        new(AzureResourceKinds.Cosmos, "MainDb"),
        new(AzureResourceKinds.FunctionApp, "Default"),
    ];

    public DockerAzureEnvironment CreateEnvironment()
        => DockerAzureEnvironment
            .For<DefaultFunctionApp>()
            .Include<CustomInfrastructure>();

    public ConfigInstance CreatePersistentConfig()
        => ConfigInstance.Create().LoadDockerAzureConfig().Build();
}

// The base class does not implement IAsyncLifetime; you add it. That keeps xunit out of the package's
// runtime dependencies, and it is what lets a xunit v3 consumer write a ValueTask adapter instead.
public sealed class DockerAzureHostedFixture : DockerAzureHostedCollectionFixture<DockerAzureHostedFixtureState>, IAsyncLifetime;

[Collection(DockerAzureHostedCollectionDefinition.CollectionName)]
public sealed class HostedSuite(DockerAzureHostedFixture fixture)
{
    [Fact]
    public async Task Uses_a_fresh_run_environment_on_top_of_one_persistent_stack()
    {
        Timeline timeline = Timeline.Create()
            .Trigger(AzureExt.Trigger.IsLive.Blob("MainStorage", AlivenessLevel.Authenticated))
            .Build();

        TimelineRun run = await timeline
            .SetupRun()
            .SetEnv(fixture.GetEnv())
            .RunAsync();

        run.EnsureRanToCompletion();
    }
}
#endregion

public sealed class NoResetState
{
    #region snippet:no-reset
    public DockerAzureEnvironment CreateEnvironment()
        => DockerAzureEnvironment
            .For<DefaultFunctionApp>()
            .UseResetMode(AzureResetMode.None);
    #endregion
}
