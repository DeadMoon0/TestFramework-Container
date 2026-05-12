using DotNet.Testcontainers.Networks;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Testcontainers.MsSql;
using Testcontainers.ServiceBus;
using TestFramework.Azure.Configuration;
using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;
using TestFramework.Core.Logging;
using TestFramework.Core.Variables;

namespace TestFramework.Container.Azure.Components;

internal sealed class MsSqlEnvComponent : DockerAzureEnvComponent
{
    public override EnvComponentIdentifier Id => DockerAzureEnvironment.MsSqlComponentId;

    public override EnvComponentReuseMode ReuseMode => EnvComponentReuseMode.PersistentContext;

    public override IReadOnlyList<EnvComponentIdentifier> Dependencies => [DockerAzureEnvironment.NetworkComponentId];

    public override async Task<object?> CreateAsync(IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        DockerAzureEnvironment dockerEnvironment = GetDockerEnvironment(environment);
        ConfigStore<SqlDatabaseConfig>? configStore = EnvComponentConfigStoreGuard.GetRequiredStore<SqlDatabaseConfig>(dockerEnvironment, serviceProvider, dockerEnvironment.UsedSqlIdentifiers, "SQL environment setup");
        INetwork network = dockerEnvironment.GetRequiredRuntimeState<INetwork>(DockerAzureEnvironment.NetworkComponentId);
        MsSqlBuilder builder = new MsSqlBuilder(dockerEnvironment.GetMsSqlImage())
            .WithPassword(dockerEnvironment.GetMsSqlPassword())
            .WithNetwork(network)
            .WithNetworkAliases(ServiceBusBuilder.DatabaseNetworkAlias);

        MsSqlContainer container = builder.Build();

        await container.StartAsync(cancellationToken).ConfigureAwait(false);

        string connectionString = container.GetConnectionString();
        await WaitForSqlReadyAsync(connectionString, cancellationToken).ConfigureAwait(false);
        ConnectionStringGuards.EnsureSql(connectionString);

        if (configStore is not null)
        {
            foreach (string identifier in dockerEnvironment.UsedSqlIdentifiers)
            {
                SqlDatabaseConfig current = configStore.GetConfig(identifier);
                configStore.AddConfig(identifier, current with { ConnectionString = connectionString });
            }
        }

        dockerEnvironment.SetRuntimeState(Id, container);
        return container;
    }

    public override async Task DeconstructAsync(object? state, IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        if (state is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task WaitForSqlReadyAsync(string connectionString, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                await using SqlConnection connection = new(connectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using SqlCommand command = new("SELECT 1;", connection);
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch when (attempt < 29)
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("The SQL container did not become ready in time.");
    }
}