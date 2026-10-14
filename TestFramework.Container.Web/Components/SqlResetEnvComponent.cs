using System.Collections.Generic;
using System.Threading.Tasks;
using TestFramework.Core.Environment;
using TestFramework.Core.Logging;
using TestFramework.Core.Steps;
using TestFramework.Web.Configuration;
using TestFramework.Web.Sql;
using TestFramework.Web.Sql.Model;

namespace TestFramework.Container.Web.Components;

/// <summary>
/// Applies each database's reset mode when this run was handed a SQL Server an earlier run started.
/// </summary>
/// <remarks>
/// <para>
/// Per run, beside a server that outlives runs. Provisioning applies the reset mode when the server is created, and
/// on a hosted stack that happens once for the whole collection - so without this, <c>RecreateDatabase</c> would
/// recreate nothing after the first run, and the README's promise that a run starts from the declared schema would
/// hold only for the run that happened to start the server.
/// </para>
/// <para>
/// A run that started its own server skips it: the server it just provisioned is already in the declared state.
/// </para>
/// </remarks>
internal sealed class SqlResetEnvComponent : WebEnvComponentBase
{
    public override EnvComponentIdentifier Id => DockerWebEnvironment.SqlResetComponentId;

    public override EnvComponentReuseMode ReuseMode => EnvComponentReuseMode.PerRun;

    public override IReadOnlyList<EnvComponentIdentifier> Dependencies => [DockerWebEnvironment.SqlServerComponentId];

    public override async Task<object?> CreateAsync(IEnvironmentProvider environment, RunContext context)
    {
        DockerWebEnvironment webEnvironment = GetWebEnvironment(environment);
        IReadOnlyList<DockerSqlDefinition> definitions = webEnvironment.GetSqlDefinitions();

        if (definitions.Count == 0 || !webEnvironment.WasHandedRunning(DockerWebEnvironment.SqlServerComponentId))
            return null;

        SqlServerComponentState sql = StateOf<SqlServerComponentState>(context, DockerWebEnvironment.SqlServerComponentId);
        SqlModelRegistry registry = SqlConfigResolver.ResolveModelRegistry(context.Services!);
        string serverConnectionString = sql.Container.GetConnectionString();

        foreach (DockerSqlDefinition definition in definitions)
        {
            DockerSqlSpec spec = definition.Build();
            SqlDatabaseEndpoint endpoint = sql.GetRequiredDatabase(definition.Identifier);

            context.Logger.LogInformation("The SQL Server was started by an earlier run, so '{0}' is reset ({1}).", spec.DatabaseName, spec.ResetMode);
            await SqlDatabaseProvisioner.ResetAsync(serverConnectionString, endpoint.HostConnectionString, spec, registry, context.Logger, context.Deadline.Token).ConfigureAwait(false);
        }

        return null;
    }

    public override Task DeconstructAsync(object? state, IEnvironmentProvider environment, RunContext context) => Task.CompletedTask;
}
