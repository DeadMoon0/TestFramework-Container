using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;
using TestFramework.Core.Exceptions;
using TestFramework.Core.Logging;
using TestFramework.Core.Variables;
using TestFramework.Web.Configuration;
using TestFramework.Web.Sql;
using TestFramework.Web.Sql.Model;

namespace TestFramework.Container.Web.Components;

/// <summary>
/// Runs SQL Server, provisions the declared databases and publishes their connection strings.
/// </summary>
internal sealed class SqlServerEnvComponent : WebEnvComponentBase
{
    public override EnvComponentIdentifier Id => DockerWebEnvironment.SqlServerComponentId;

    public override EnvComponentReuseMode ReuseMode => EnvComponentReuseMode.PersistentContext;

    public override IReadOnlyList<EnvComponentIdentifier> Dependencies => [DockerWebEnvironment.NetworkComponentId];

    public override async Task<object?> CreateAsync(IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(logger);

        DockerWebEnvironment webEnvironment = GetWebEnvironment(environment);
        IReadOnlyList<DockerSqlDefinition> definitions = webEnvironment.GetSqlDefinitions();

        // The application component depends on this one so that ordering is guaranteed when it needs
        // a database. When nothing declares one, there is nothing to start.
        if (definitions.Count == 0)
        {
            logger.LogInformation("No database was declared, so no SQL Server container is started.");
            return null;
        }

        WebConfigStore<SqlConfig> configStore = GetRequiredConfigStore(serviceProvider);
        SqlModelRegistry registry = SqlConfigResolver.ResolveModelRegistry(serviceProvider);
        INetwork network = webEnvironment.GetRequiredRuntimeState<INetwork>(DockerWebEnvironment.NetworkComponentId);

        MsSqlContainer container = MsSqlContainerFactory.Create(
            new MsSqlContainerOptions(
                webEnvironment.SqlImage,
                webEnvironment.SqlPassword,
                webEnvironment.SqlMemoryLimitMb,
                [DockerWebDefaults.MsSqlNetworkAlias]),
            network);

        await container.StartAsync(cancellationToken).ConfigureAwait(false);

        // A started container is not a usable server, and publishing an address before the server
        // answers turns a startup race into a confusing failure much later.
        string serverConnectionString = container.GetConnectionString();
        await ContainerReadiness.WaitForSqlAsync(serverConnectionString, DockerWebDefaults.MsSqlReadinessTimeout, "the SQL Server container", cancellationToken).ConfigureAwait(false);

        Dictionary<string, SqlDatabaseEndpoint> databases = [];
        foreach (DockerSqlDefinition definition in definitions)
        {
            DockerSqlSpec spec = definition.Build();
            await SqlDatabaseProvisioner.EnsureDatabaseAsync(serverConnectionString, spec, logger, cancellationToken).ConfigureAwait(false);

            SqlDatabaseEndpoint endpoint = new(
                ContainerEndpoints.HostSqlConnectionString(container, spec.DatabaseName, MsSqlContainerOptions.UserName, webEnvironment.SqlPassword),
                ContainerEndpoints.NetworkSqlConnectionString(DockerWebDefaults.MsSqlNetworkAlias, spec.DatabaseName, MsSqlContainerOptions.UserName, webEnvironment.SqlPassword));

            await SqlDatabaseProvisioner.ApplySchemaAsync(endpoint.HostConnectionString, spec, registry, logger, cancellationToken).ConfigureAwait(false);

            Publish(configStore, definition.Identifier, endpoint.HostConnectionString, webEnvironment.SqlPassword);
            databases[definition.Identifier] = endpoint;

            logger.LogInformation("SQL identifier '{0}' is served by the database '{1}'.", definition.Identifier.ToString(), spec.DatabaseName);
        }

        SqlServerComponentState state = new(container, databases);
        webEnvironment.SetRuntimeState(Id, state);
        return state;
    }

    public override async Task DeconstructAsync(object? state, IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        if (state is SqlServerComponentState sqlState)
            await ContainerDockerCommands.ForceRemoveContainerAsync(sqlState.Container, cancellationToken).ConfigureAwait(false);
        else if (state is IContainer container)
            await ContainerDockerCommands.ForceRemoveContainerAsync(container, cancellationToken).ConfigureAwait(false);
        else if (state is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
    }

    private static WebConfigStore<SqlConfig> GetRequiredConfigStore(IServiceProvider serviceProvider)
        => serviceProvider.GetService<WebConfigStore<SqlConfig>>()
        ?? throw new FrameworkConfigurationException(
            "The run has no SQL configuration store, so the container has nowhere to publish its connection strings. "
            + "Call LoadWebConfig() on the config instance the run is set up with.");

    private static void Publish(WebConfigStore<SqlConfig> configStore, string identifier, string connectionString, string password)
    {
        SqlConfig current = configStore.TryGetConfig(identifier, out SqlConfig? existing) && existing is not null
            ? existing
            : new SqlConfig();

        // The container owns the whole connection. Leaving a configured server, or integrated
        // security meant for a developer machine, in place would silently point the run elsewhere.
        configStore.AddConfig(identifier, current with
        {
            ConnectionString = connectionString,
            Server = null,
            Database = null,
            IntegratedSecurity = false,
            UserName = MsSqlContainerOptions.UserName,
            Password = password,
            TrustServerCertificate = true,
        });
    }
}
