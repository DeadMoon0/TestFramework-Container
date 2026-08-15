using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Azure.Configuration;
using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Azure.DB.CosmosDB;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;
using TestFramework.Core.Exceptions;
using TestFramework.Core.Logging;
using TestFramework.Core.Variables;

namespace TestFramework.Container.Azure.Components;

internal sealed class CosmosDbEnvComponent : DockerAzureEnvComponent
{
    private static readonly TimeSpan GatewayReadinessTimeout = TimeSpan.FromMinutes(2);

    public override EnvComponentIdentifier Id => DockerAzureEnvironment.CosmosDbComponentId;

    public override EnvComponentReuseMode ReuseMode => EnvComponentReuseMode.PersistentContext;

    public override IReadOnlyList<EnvComponentIdentifier> Dependencies => [DockerAzureEnvironment.NetworkComponentId];

    public override async Task<object?> CreateAsync(IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        DockerAzureEnvironment dockerEnvironment = GetDockerEnvironment(environment);
        if (dockerEnvironment.UsedCosmosIdentifiers.Count == 0)
        {
            logger.LogInformation("Skipping Cosmos environment setup because no Cosmos identifiers were requested.");
            return null;
        }

        ConfigStore<CosmosContainerDbConfig>? configStore = EnvComponentConfigStoreGuard.GetRequiredStore<CosmosContainerDbConfig>(dockerEnvironment, serviceProvider, dockerEnvironment.UsedCosmosIdentifiers, "Cosmos environment setup");
        INetwork network = dockerEnvironment.GetRequiredRuntimeState<INetwork>(DockerAzureEnvironment.NetworkComponentId);
        string cosmosImage = dockerEnvironment.GetCosmosDbImage();
        ContainerBuilder builder = new ContainerBuilder(cosmosImage)
            .WithNetwork(network)
            .WithNetworkAliases(DockerAzureEnvironment.CosmosDbNetworkAlias)
            .WithPortBinding(8080, true)
            .WithPortBinding(8081, true)
            .WithPortBinding(1234, true)
            .WithCreateParameterModifier(ContainerPortBinding.Apply);

        if (cosmosImage.Contains("vnext-preview", StringComparison.OrdinalIgnoreCase))
            builder = builder.WithCommand("--protocol", "https");

        IContainer container = builder.Build();

        await container.StartAsync(cancellationToken).ConfigureAwait(false);

        string connectionString = dockerEnvironment.GetEndpointMap().CreateCosmosConnectionString(container);
        ConnectionStringGuards.EnsureCosmos(connectionString);

        using CosmosClient client = new(connectionString, new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            HttpClientFactory = () => new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            }),
        });
        await WaitForGatewayAsync(client, DescribeCosmosEndpoint(connectionString), logger, cancellationToken).ConfigureAwait(false);

        if (configStore is not null)
        {
            foreach (string identifier in dockerEnvironment.UsedCosmosIdentifiers)
            {
                CosmosContainerDbConfig current = configStore.GetConfig(identifier);
                CosmosContainerDbConfig updated = current with { ConnectionString = connectionString };
                configStore.AddConfig(identifier, updated);

                if (dockerEnvironment.CosmosPartitionKeyPaths.TryGetValue(identifier, out string? partitionKeyPath))
                    await DeploySchemaAsync(updated.ConnectionString, identifier, updated, partitionKeyPath, logger, cancellationToken).ConfigureAwait(false);
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
    private static async Task WaitForGatewayAsync(CosmosClient client, string endpoint, ScopedLogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation($"Waiting up to {GatewayReadinessTimeout:g} for the Cosmos gateway at {endpoint}.");

        DateTime deadline = DateTime.UtcNow.Add(GatewayReadinessTimeout);
        Exception? lastError = null;
        int attempt = 0;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;
            try
            {
                await client.ReadAccountAsync().ConfigureAwait(false);
                logger.LogInformation($"Cosmos gateway is ready after {attempt} attempt(s).");
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastError = exception;
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new FrameworkTimeoutException(
            $"The Cosmos emulator gateway at {endpoint} did not become ready within {GatewayReadinessTimeout:g} ({attempt} attempts). Last failure: {(lastError is null ? "(none)" : $"{lastError.GetType().Name}: {lastError.Message}")}",
            lastError);
    }

    private static async Task DeploySchemaAsync(string connectionString, string identifier, CosmosContainerDbConfig config, string partitionKeyPath, ScopedLogger logger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Stopwatch stopwatch = Stopwatch.StartNew();
        await CosmosSchemaRestClient.EnsureDatabaseAndContainerExistAsync(connectionString, config.DatabaseName, config.ContainerName, partitionKeyPath, cancellationToken).ConfigureAwait(false);
        logger.LogInformation($"Deployed the Cosmos schema for '{identifier}': {config.DatabaseName}/{config.ContainerName} ({partitionKeyPath}) in {stopwatch.Elapsed:g}.");
    }

    /// <summary>
    /// Reduces a Cosmos connection string to its account endpoint so the emulator key never reaches a run log.
    /// </summary>
    private static string DescribeCosmosEndpoint(string connectionString)
    {
        foreach (string part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("AccountEndpoint=", StringComparison.OrdinalIgnoreCase))
                return part["AccountEndpoint=".Length..];
        }

        return "(unknown endpoint)";
    }
}