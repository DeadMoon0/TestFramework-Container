using TestFramework.Core.Steps;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Testcontainers.MsSql;
using Testcontainers.ServiceBus;
using TestFramework.Azure.Configuration;
using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Core.Artifacts;
using TestFramework.Azure;
using TestFramework.Core.Environment.Graph;
using TestFramework.Core.Environment;
using TestFramework.Core.Logging;
using TestFramework.Core.Variables;

namespace TestFramework.Container.Azure.Components;

internal sealed class MsSqlEnvComponent : DockerAzureEnvComponent
{
    public override EnvComponentIdentifier Id => DockerAzureEnvironment.MsSqlComponentId;

    public override EnvComponentReuseMode ReuseMode => EnvComponentReuseMode.PersistentContext;

    public override IReadOnlyList<EnvComponentIdentifier> Dependencies => [DockerAzureEnvironment.NetworkComponentId];

    public override async Task<object?> CreateAsync(IEnvironmentProvider environment, RunContext context)
    {
        DockerAzureEnvironment dockerEnvironment = GetDockerEnvironment(environment);

        // The Service Bus emulator is backed by this very container, so a SQL-only guard would break every Service Bus run.
        if (dockerEnvironment.UsedSqlIdentifiers.Count == 0 && dockerEnvironment.UsedServiceBusIdentifiers.Count == 0)
        {
            context.Logger.LogInformation("Skipping SQL environment setup because neither SQL nor Service Bus identifiers were requested.");
            return null;
        }

        EnvironmentResources resources = PublishOn(context);
        INetwork network = dockerEnvironment.GetRequiredRuntimeState<INetwork>(DockerAzureEnvironment.NetworkComponentId);
        string msSqlImage = dockerEnvironment.GetMsSqlImage();

        // Recorded on the run because nobody stated it: the tag lives in this package's defaults, so a run
        // that passed could not say which image proved it and a bump would change every consumer's result
        // with no diff on their side. §5's third demand, and the reason a finished run is worth handing over.
        context.EffectiveSettings.Record(ImageSource, "mssql:Image", msSqlImage);

        MsSqlContainer container = MsSqlContainerFactory.Create(
            new MsSqlContainerOptions(
                msSqlImage,
                dockerEnvironment.GetMsSqlPassword(),
                dockerEnvironment.GetMsSqlMemoryLimitMb(),
                [ServiceBusBuilder.DatabaseNetworkAlias]),
            network);

        await container.StartAsync(context.Deadline.Token).ConfigureAwait(false);

        string connectionString = container.GetConnectionString();
        await ContainerReadiness.WaitForSqlAsync(connectionString, dockerEnvironment.GetMsSqlReadinessTimeout(), "the SQL container", context.Deadline.Token).ConfigureAwait(false);
        ConnectionStringGuards.EnsureSql(connectionString);

        // The last of these in the family, and it published rather than writing. A DbContext used to take its
        // connection string when its registration was built, from a service provider with no run in sight, so
        // writing this store was the only channel that reached one. SqlDbContextRegistry now builds a context
        // from options the framework points at the run's database, which removed the need for the channel
        // instead of hiding it.
        foreach (string identifier in dockerEnvironment.UsedSqlIdentifiers)
        {
            resources.Produce(
                AzureEnvironmentResourceKinds.SqlKind,
                identifier,
                ValueNames.ConnectionString,
                ResourceVantage.Host,
                connectionString);
        }

        dockerEnvironment.SetRuntimeState(Id, container);
        return container;
    }

    public override async Task DeconstructAsync(object? state, IEnvironmentProvider environment, RunContext context)
    {
        // The CLI route first, like every other container teardown in the family: disposing through
        // the client can leave a container behind when the daemon is busy.
        if (state is IContainer container)
            await ContainerDockerCommands.ForceRemoveContainerAsync(container, context.Deadline.Token).ConfigureAwait(false);
        else if (state is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
    }

}
