using TestFramework.Config;
using TestFramework.Core.Steps;
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
using TestFramework.Azure.DB.CosmosDb;
using TestFramework.Core.Artifacts;
using TestFramework.Azure;
using TestFramework.Core.Environment.Graph;
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

    public override async Task<object?> CreateAsync(IEnvironmentProvider environment, RunContext context)
    {
        DockerAzureEnvironment dockerEnvironment = GetDockerEnvironment(environment);
        if (dockerEnvironment.UsedCosmosDbIdentifiers.Count == 0)
        {
            context.Logger.LogInformation("Skipping Cosmos environment setup because no Cosmos identifiers were requested.");
            return null;
        }

        EnvComponentResourceGuard.EnsureSupplied(context, AzureResourceKinds.CosmosDbKind, dockerEnvironment.UsedCosmosDbIdentifiers, "Cosmos environment setup");
        INetwork network = StateOf<INetwork>(context, DockerAzureEnvironment.NetworkComponentId);
        string cosmosImage = dockerEnvironment.GetCosmosDbImage();

        // Recorded on the run because nobody stated it: the tag lives in this package's defaults, so a run
        // that passed could not say which image proved it and a bump would change every consumer's result
        // with no diff on their side. §5's third demand, and the reason a finished run is worth handing over.
        context.EffectiveSettings.Record(ImageSource, "cosmos:Image", cosmosImage);
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

        return await ContainerStartup.BringUpAsync(container, async () =>
        {
            await container.StartAsync(context.Deadline.Token).ConfigureAwait(false);

            string connectionString = dockerEnvironment.GetEndpointMap().CreateCosmosConnectionString(container);
            ConnectionStringGuards.EnsureCosmos(connectionString);

            using CosmosClient client = new(connectionString, CosmosEmulator.ClientOptions());
            await WaitForGatewayAsync(client, CosmosEmulator.DescribeEndpoint(connectionString), context.Logger, context.Deadline.Token).ConfigureAwait(false);

            // Published rather than written back into the Azure package's configuration store. That store holds
            // what a person declared, and a mapped port is not something a person can declare - it is chosen when
            // the container starts. Where a resource ended up belongs to the run.
            foreach (string identifier in dockerEnvironment.UsedCosmosDbIdentifiers)
            {
                PublishOn(context).Produce(
                    AzureResourceKinds.CosmosDbKind,
                    identifier,
                    ValueNames.ConnectionString,
                    ResourceVantage.Host,
                    connectionString);

                if (!dockerEnvironment.CosmosPartitionKeyPaths.TryGetValue(identifier, out string? partitionKeyPath))
                    continue;

                // Read back from the run, which is where the address above has just gone. The declared entry
                // names the database and the container; this one also carries the endpoint the emulator ended
                // up on, so nothing has to staple the two together by hand.
                CosmosDbConfig target = context.Configured<CosmosDbConfig>(identifier);
                await DeploySchemaAsync(identifier, target, partitionKeyPath, context.Logger, context.Deadline.Token).ConfigureAwait(false);
            }

            return container;
        }).ConfigureAwait(false);
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
    private static async Task WaitForGatewayAsync(CosmosClient client, string endpoint, ScopedLogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation($"Waiting up to {GatewayReadinessTimeout:g} for the Cosmos gateway at {endpoint}.");

        int attempts = 0;

        // The account read takes no token; the readiness window bounds it all the same.
        await ContainerReadiness.WaitUntilAsync(
            async _ =>
            {
                attempts++;
                await client.ReadAccountAsync().ConfigureAwait(false);
                return true;
            },
            static _ => true,
            GatewayReadinessTimeout,
            (made, lastError) => new FrameworkTimeoutException(
                $"The Cosmos emulator gateway at {endpoint} did not become ready within {GatewayReadinessTimeout:g} ({made} attempts). Last failure: {(lastError is null ? "(none)" : $"{lastError.GetType().Name}: {lastError.Message}")}",
                lastError),
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation($"Cosmos gateway is ready after {attempts} attempt(s).");
    }

    private static async Task DeploySchemaAsync(string identifier, CosmosDbConfig config, string partitionKeyPath, ScopedLogger logger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The config already carries the container's mapped connection string, so it is the single
        // source of the endpoint the schema is deployed against.
        Stopwatch stopwatch = Stopwatch.StartNew();
        await CosmosSchema.EnsureExistsAsync(config, partitionKeyPath, cancellationToken).ConfigureAwait(false);
        logger.LogInformation($"Deployed the Cosmos schema for '{identifier}': {config.DatabaseName}/{config.ContainerName} ({partitionKeyPath}) in {stopwatch.Elapsed:g}.");
    }

}