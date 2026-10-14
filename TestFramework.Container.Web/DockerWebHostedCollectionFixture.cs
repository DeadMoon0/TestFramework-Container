using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Config;
using TestFramework.Config.Builder.InstanceBuilder;
using TestFramework.Core.Environment;
using TestFramework.Core.Exceptions;
using TestFramework.Web.Extensions;

namespace TestFramework.Container.Web;

/// <summary>
/// Describes a hosted web stack: the environment it serves and the configuration it boots with.
/// </summary>
public interface IDockerWebHostedFixtureState
{
    /// <summary>
    /// How long the one-off bootstrap of the network and SQL Server may take.
    /// </summary>
    TimeSpan PersistentSetupTimeout => DockerWebDefaults.PersistentSetupTimeout;

    /// <summary>
    /// The environment every run of the collection uses, declared once.
    /// </summary>
    DockerWebEnvironment CreateEnvironment();

    /// <summary>
    /// The configuration the stack boots with, and that every run's configuration is layered on.
    /// </summary>
    /// <remarks>
    /// It registers what the SQL Server needs to provision the declared databases - the models a schema is
    /// generated from among them - because provisioning happens here, once, before any run.
    /// </remarks>
    ConfigInstance CreatePersistentConfig();
}

/// <summary>
/// Starts one Docker network and one SQL Server for a whole test collection, and hands every run an environment
/// built on them.
/// </summary>
/// <typeparam name="TState">Describes the environment and the persistent configuration.</typeparam>
/// <remarks>
/// <para>
/// The network and the SQL Server outlive every run; applications, stubs and sites are still started per run.
/// Each run that is handed the running server applies every database's <see cref="SqlResetMode"/> before
/// anything else touches it, so <c>RecreateDatabase</c> means the same thing here as on a server the run started.
/// </para>
/// <para>
/// This deliberately does not implement <c>Xunit.IAsyncLifetime</c>, for the reason
/// <c>DockerAzureHostedCollectionFixture</c> gives: it would make xunit a runtime dependency of a library. The
/// methods have the v2 signatures, so adding the interface on your own fixture is one line:
/// </para>
/// <code>
/// public sealed class MyFixture : DockerWebHostedCollectionFixture&lt;MyState&gt;, IAsyncLifetime;
/// </code>
/// </remarks>
public class DockerWebHostedCollectionFixture<TState>
    where TState : IDockerWebHostedFixtureState, new()
{
    private readonly TState _state = new();
    private PersistentEnvironmentContext<DockerWebPersistentSetup>? _persistentContext;
    private ConfigInstance? _persistentConfig;

    /// <summary>
    /// Starts the network and the SQL Server. Signature-compatible with <c>Xunit.IAsyncLifetime</c> v2.
    /// </summary>
    public async Task InitializeAsync()
    {
        ConfigInstance persistentConfig = _state.CreatePersistentConfig();
        DockerWebPersistentSetup setup = new(_state.CreateEnvironment(), persistentConfig, _state.PersistentSetupTimeout);

        _persistentConfig = persistentConfig;
        _persistentContext = await PersistentEnvironmentContext<DockerWebPersistentSetup>
            .CreateAsync(setup, persistentConfig.BuildServiceProvider(), CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Tears the network and the SQL Server down. Signature-compatible with <c>Xunit.IAsyncLifetime</c> v2.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (_persistentContext is not null)
            await _persistentContext.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The environment for one run, on the collection's network and SQL Server.
    /// </summary>
    public IEnvironmentProvider GetEnv()
        => this.GetEnv(null);

    /// <summary>
    /// The environment for one run, on the collection's network and SQL Server, with this run's configuration
    /// layered over the persistent one.
    /// </summary>
    /// <param name="configure">Adds to this run's configuration.</param>
    public IEnvironmentProvider GetEnv(Action<IConfigInstanceBuilder>? configure)
    {
        PersistentEnvironmentContext<DockerWebPersistentSetup> persistentContext = _persistentContext ?? throw NotInitialized();
        ConfigInstance persistentConfig = _persistentConfig ?? throw NotInitialized();

        IConfigInstanceBuilder builder = persistentConfig.SetupSubInstance();
        configure?.Invoke(builder);

        return new HostedRunEnvironment(persistentContext.CreateEnvironment(), builder.Build().BuildServiceProvider());
    }

    private static FrameworkStateException NotInitialized()
        => new("The hosted Docker web fixture has not finished initialization.")
        {
            RecoverySteps = ["Await InitializeAsync before asking for an environment - xunit does when the fixture implements IAsyncLifetime."],
        };
}

/// <summary>
/// The persistent slice of a hosted web stack: the network, and the SQL Server when a database is declared.
/// </summary>
internal sealed class DockerWebPersistentSetup : IConfigPersistentEnvironmentSetup
{
    private readonly DockerWebEnvironment _environment;
    private readonly ConfigInstance _persistentConfig;
    private readonly TimeSpan _persistentSetupTimeout;

    /// <summary>
    /// An empty stack - what <c>PersistentEnvironmentContext</c>'s own <c>CreateAsync()</c> builds; the fixture
    /// always passes its state's.
    /// </summary>
    public DockerWebPersistentSetup()
        : this(new DockerWebEnvironment(), ConfigInstance.Create().LoadWebConfig().Build(), DockerWebDefaults.PersistentSetupTimeout)
    {
    }

    public DockerWebPersistentSetup(DockerWebEnvironment environment, ConfigInstance persistentConfig, TimeSpan persistentSetupTimeout)
    {
        _environment = environment.CloneDefinitions();
        _persistentConfig = persistentConfig;
        _persistentSetupTimeout = persistentSetupTimeout;
    }

    public IEnvironmentProvider CreateEnvironment() => _environment.CloneDefinitions();

    public ConfigInstance CreatePersistentConfig() => _persistentConfig;

    public IReadOnlyCollection<EnvComponentIdentifier> GetPersistentComponentIdentifiers()
        => _environment.GetSqlDefinitions().Count > 0
            ? [DockerWebEnvironment.NetworkComponentId, DockerWebEnvironment.SqlServerComponentId]
            : [DockerWebEnvironment.NetworkComponentId];

    public TimeSpan GetPersistentSetupTimeout() => _persistentSetupTimeout;
}
