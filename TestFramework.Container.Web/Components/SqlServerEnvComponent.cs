using TestFramework.Core.Steps;
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
using TestFramework.Core.Environment.Graph;
using TestFramework.Web;
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

    public override async Task<object?> CreateAsync(IEnvironmentProvider environment, RunContext context)
    {
        ArgumentNullException.ThrowIfNull(context.Services);
        ArgumentNullException.ThrowIfNull(context.Logger);

        DockerWebEnvironment webEnvironment = GetWebEnvironment(environment);
        IReadOnlyList<DockerSqlDefinition> definitions = webEnvironment.GetSqlDefinitions();

        // The application component depends on this one so that ordering is guaranteed when it needs
        // a database. When nothing declares one, there is nothing to start.
        if (definitions.Count == 0)
        {
            context.Logger.LogInformation("No database was declared, so no SQL Server container is started.");
            return null;
        }

        SqlModelRegistry registry = SqlConfigResolver.ResolveModelRegistry(context.Services);
        INetwork network = webEnvironment.GetRequiredRuntimeState<INetwork>(DockerWebEnvironment.NetworkComponentId);

        // Recorded on the run because nobody stated it: the tag lives in this package's defaults, so a
        // run that passed could not otherwise say which image proved it. §5's third demand.
        context.EffectiveSettings.Record(ImageSource, "sql:Image", webEnvironment.SqlImage);

        MsSqlContainer container = MsSqlContainerFactory.Create(
            new MsSqlContainerOptions(
                webEnvironment.SqlImage,
                webEnvironment.SqlPassword,
                webEnvironment.SqlMemoryLimitMb,
                [DockerWebDefaults.MsSqlNetworkAlias]),
            network);

        await container.StartAsync(context.Deadline.Token).ConfigureAwait(false);

        // A started container is not a usable server, and publishing an address before the server
        // answers turns a startup race into a confusing failure much later.
        string serverConnectionString = container.GetConnectionString();
        await ContainerReadiness.WaitForSqlAsync(serverConnectionString, DockerWebDefaults.MsSqlReadinessTimeout, "the SQL Server container", context.Deadline.Token).ConfigureAwait(false);

        Dictionary<string, SqlDatabaseEndpoint> databases = [];
        foreach (DockerSqlDefinition definition in definitions)
        {
            DockerSqlSpec spec = definition.Build();
            await SqlDatabaseProvisioner.EnsureDatabaseAsync(serverConnectionString, spec, context.Logger, context.Deadline.Token).ConfigureAwait(false);

            SqlDatabaseEndpoint endpoint = new(
                ContainerEndpoints.HostSqlConnectionString(container, spec.DatabaseName, MsSqlContainerOptions.UserName, webEnvironment.SqlPassword),
                ContainerEndpoints.NetworkSqlConnectionString(DockerWebDefaults.MsSqlNetworkAlias, spec.DatabaseName, MsSqlContainerOptions.UserName, webEnvironment.SqlPassword));

            await SqlDatabaseProvisioner.ApplySchemaAsync(endpoint.HostConnectionString, spec, registry, context.Logger, context.Deadline.Token).ConfigureAwait(false);

            Publish(context, definition.Identifier, endpoint);
            databases[definition.Identifier] = endpoint;

            context.Logger.LogInformation("SQL identifier '{0}' is served by the database '{1}'.", definition.Identifier.ToString(), spec.DatabaseName);
        }

        SqlServerComponentState state = new(container, databases);
        webEnvironment.SetRuntimeState(Id, state);
        return state;
    }

    public override async Task DeconstructAsync(object? state, IEnvironmentProvider environment, RunContext context)
    {
        if (state is SqlServerComponentState sqlState)
            await ContainerDockerCommands.ForceRemoveContainerAsync(sqlState.Container, context.Deadline.Token).ConfigureAwait(false);
        else if (state is IContainer container)
            await ContainerDockerCommands.ForceRemoveContainerAsync(container, context.Deadline.Token).ConfigureAwait(false);
        else if (state is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes how to reach this database, for both viewpoints.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Published rather than written back into <c>TestFramework.Web</c>'s configuration store. That store
    /// holds what a person declared; the port this container ended up on is a resource value, and the run
    /// holds those.
    /// </para>
    /// <para>
    /// Both connection strings, which the store could not express: the test process reaches the mapped host
    /// port and a peer container reaches the network alias, and a store with one <c>ConnectionString</c>
    /// forced everything inside the network to be handed the address that only works outside it.
    /// </para>
    /// <para>
    /// Each string already carries the container's own credentials, so nothing about them is published
    /// separately - a password does not belong in a value store that a run can snapshot and log. The
    /// store write this replaces also nulled a declared server and integrated-security flag, because a
    /// container owns the whole connection; a produced value cannot un-declare anything, so for a database
    /// that is both configured and containerised those declared credentials are still applied on top. See
    /// entry 20 of the debt ledger - it needs an origin-aware read to close properly.
    /// </para>
    /// </remarks>
    private static void Publish(RunContext context, string identifier, SqlDatabaseEndpoint endpoint)
    {
        EnvironmentResources resources = PublishOn(context);

        resources.Produce(WebEnvironmentResourceKinds.SqlKind, identifier, ValueNames.ConnectionString, ResourceVantage.Host, endpoint.HostConnectionString);
        resources.Produce(WebEnvironmentResourceKinds.SqlKind, identifier, ValueNames.ConnectionString, ResourceVantage.Network, endpoint.NetworkConnectionString);
    }
}
