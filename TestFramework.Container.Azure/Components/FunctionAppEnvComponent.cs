using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Extensions.DependencyInjection;
using System.Data.Common;
using TestFramework.Azure.Configuration;
using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;
using TestFramework.Core.Logging;
using TestFramework.Core.Variables;

namespace TestFramework.Container.Azure.Components;

internal sealed class FunctionAppEnvComponent : EnvComponent
{
    private const string FunctionAppRoot = "/home/site/wwwroot";

    public override EnvComponentIdentifier Id => DockerAzureEnvironment.FunctionAppComponentId;

    public override IReadOnlyList<EnvComponentIdentifier> Dependencies =>
    [
        DockerAzureEnvironment.NetworkComponentId,
        DockerAzureEnvironment.AzuriteComponentId,
        DockerAzureEnvironment.CosmosDbComponentId,
        DockerAzureEnvironment.ServiceBusComponentId,
        DockerAzureEnvironment.MsSqlComponentId,
    ];

    public override async Task<object?> CreateAsync(IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        DockerAzureEnvironment dockerEnvironment = (DockerAzureEnvironment)environment;
        if (dockerEnvironment.UsedFunctionAppIdentifiers.Count == 0)
            return Array.Empty<IContainer>();

        ConfigStore<FunctionAppConfig>? functionStore = EnvComponentConfigStoreGuard.GetRequiredStore<FunctionAppConfig>(serviceProvider, dockerEnvironment.UsedFunctionAppIdentifiers, "Function App environment setup");
        INetwork network = dockerEnvironment.GetRequiredRuntimeState<INetwork>(DockerAzureEnvironment.NetworkComponentId);
        List<IContainer> containers = [];

        foreach (string identifier in dockerEnvironment.UsedFunctionAppIdentifiers)
        {
            DockerFunctionAppRegistration registration = dockerEnvironment.Options.FunctionApps.FirstOrDefault(x => string.Equals(x.Identifier, identifier, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"No Docker Function App registration was configured for identifier '{identifier}'.");

            FunctionAppLocation location = ResolveFunctionAppLocation(registration.FunctionType);
            Dictionary<string, string> appSettings = BuildAppSettings(dockerEnvironment, serviceProvider, registration);
            appSettings["AzureFunctionsJobHost__Logging__Console__IsEnabled"] = "true";
            appSettings["AzureWebJobsScriptRoot"] = FunctionAppRoot;
            appSettings["FUNCTIONS_WORKER_RUNTIME"] = "dotnet-isolated";
            appSettings["ASPNETCORE_URLS"] = "http://0.0.0.0:80";
            appSettings["PORT"] = "80";
            appSettings["WEBSITES_PORT"] = "80";

            ContainerBuilder builder = new ContainerBuilder(registration.Image)
                .WithNetwork(network)
                .WithPortBinding(80, true)
                .WithBindMount(location.OutputDirectory, FunctionAppRoot, AccessMode.ReadOnly);

            foreach ((string key, string value) in appSettings)
                builder = builder.WithEnvironment(key, value);

            IContainer container = builder.Build();

            await container.StartAsync(cancellationToken).ConfigureAwait(false);

            string baseUrl = $"http://{container.Hostname}:{container.GetMappedPublicPort(80)}/";
            await WaitForHttpReadyAsync(baseUrl, cancellationToken).ConfigureAwait(false);

            FunctionAppConfig current = functionStore!.GetConfig(identifier);
            functionStore.AddConfig(identifier, current with { BaseUrl = baseUrl });

            containers.Add(container);
        }

        return containers;
    }

    public override async Task DeconstructAsync(object? state, IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        if (state is IEnumerable<IContainer> containers)
        {
            foreach (IContainer container in containers)
            {
                await container.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static FunctionAppLocation ResolveFunctionAppLocation(Type functionType)
    {
        string? assemblyLocation = functionType.Assembly.Location;
        if (string.IsNullOrWhiteSpace(assemblyLocation))
            throw new InvalidOperationException($"Could not resolve an assembly location for Function App type '{functionType.FullName}'.");

        string? outputDirectory = Path.GetDirectoryName(assemblyLocation);
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
            throw new DirectoryNotFoundException($"Could not locate the Function App output directory for '{functionType.FullName}'.");

        string assemblyName = functionType.Assembly.GetName().Name ?? throw new InvalidOperationException("The Function App assembly name could not be resolved.");
        string projectDirectory = ResolveProjectDirectory(assemblyName, outputDirectory);
        string candidateOutputDirectory = Path.Combine(projectDirectory, "bin", "Debug", "net8.0");
        if (File.Exists(Path.Combine(candidateOutputDirectory, "host.json")) && File.Exists(Path.Combine(candidateOutputDirectory, $"{assemblyName}.dll")))
            outputDirectory = candidateOutputDirectory;

        return new FunctionAppLocation(projectDirectory, outputDirectory);
    }

    private static string ResolveProjectDirectory(string assemblyName, string startDirectory)
    {
        for (DirectoryInfo? current = new(startDirectory); current is not null; current = current.Parent)
        {
            string[] matches = Directory.GetFiles(current.FullName, $"{assemblyName}.csproj", SearchOption.AllDirectories);
            if (matches.Length == 1)
                return Path.GetDirectoryName(matches[0])!;
        }

        throw new DirectoryNotFoundException($"Could not locate the project directory for Function App assembly '{assemblyName}'.");
    }

    private static Dictionary<string, string> BuildAppSettings(DockerAzureEnvironment environment, IServiceProvider serviceProvider, DockerFunctionAppRegistration registration)
    {
        Dictionary<string, string> settings = new(StringComparer.OrdinalIgnoreCase)
        { ["AzureWebJobsFeatureFlags"] = "EnableWorkerIndexing" };

        foreach ((string key, string value) in registration.AdditionalSettings)
            settings[key] = value;

        if (registration.StorageIdentifier is not null)
        {
            StorageAccountConfig storage = serviceProvider.GetRequiredService<ConfigStore<StorageAccountConfig>>().GetConfig(registration.StorageIdentifier);
            string rewritten = RewriteStorageConnectionString(storage.ConnectionString);
            if (registration.StorageConnectionSettingName is not null)
                settings[registration.StorageConnectionSettingName] = rewritten;
            if (registration.HostStorageSettingName is not null)
                settings[registration.HostStorageSettingName] = rewritten;
            if (registration.StorageTableNameSettingName is not null)
                settings[registration.StorageTableNameSettingName] = storage.TableContainerNameRequired;
        }

        if (registration.CosmosIdentifier is not null)
        {
            CosmosContainerDbConfig cosmos = serviceProvider.GetRequiredService<ConfigStore<CosmosContainerDbConfig>>().GetConfig(registration.CosmosIdentifier);
            settings[registration.CosmosConnectionSettingName ?? "CosmosDbConnectionString"] = RewriteCosmosConnectionString(cosmos.ConnectionString);
            if (registration.CosmosDatabaseSettingName is not null)
                settings[registration.CosmosDatabaseSettingName] = cosmos.DatabaseName;
            if (registration.CosmosContainerSettingName is not null)
                settings[registration.CosmosContainerSettingName] = cosmos.ContainerName;
        }

        if (registration.ServiceBusTriggerIdentifier is not null)
        {
            ServiceBusConfig serviceBus = serviceProvider.GetRequiredService<ConfigStore<ServiceBusConfig>>().GetConfig(registration.ServiceBusTriggerIdentifier);
            settings[registration.ServiceBusTriggerConnectionSettingName ?? "ServiceBusTriggerConnection"] = RewriteServiceBusConnectionString(serviceBus.ConnectionString);
            if (registration.ServiceBusTriggerEntitySettingName is not null)
                settings[registration.ServiceBusTriggerEntitySettingName] = serviceBus.TopicName ?? serviceBus.QueueName ?? throw new InvalidOperationException($"Service Bus identifier '{registration.ServiceBusTriggerIdentifier}' does not define a queue or topic name.");
            if (registration.ServiceBusTriggerSubscriptionSettingName is not null && serviceBus.SubscriptionName is not null)
                settings[registration.ServiceBusTriggerSubscriptionSettingName] = serviceBus.SubscriptionName;
        }

        if (registration.ServiceBusReplyIdentifier is not null)
        {
            ServiceBusConfig serviceBus = serviceProvider.GetRequiredService<ConfigStore<ServiceBusConfig>>().GetConfig(registration.ServiceBusReplyIdentifier);
            settings[registration.ServiceBusReplyConnectionSettingName ?? "ServiceBusReplyConnectionString"] = RewriteServiceBusConnectionString(serviceBus.ConnectionString);
            if (registration.ServiceBusReplyEntitySettingName is not null)
                settings[registration.ServiceBusReplyEntitySettingName] = serviceBus.TopicName ?? serviceBus.QueueName ?? throw new InvalidOperationException($"Service Bus identifier '{registration.ServiceBusReplyIdentifier}' does not define a queue or topic name.");
        }

        return settings;
    }

    private static string RewriteStorageConnectionString(string connectionString)
    {
        DbConnectionStringBuilder builder = CreateBuilder(connectionString);
        builder["BlobEndpoint"] = RewriteEndpoint((string)builder["BlobEndpoint"], DockerAzureEnvironment.AzuriteNetworkAlias, 10000);
        builder["QueueEndpoint"] = RewriteEndpoint((string)builder["QueueEndpoint"], DockerAzureEnvironment.AzuriteNetworkAlias, 10001);
        builder["TableEndpoint"] = RewriteEndpoint((string)builder["TableEndpoint"], DockerAzureEnvironment.AzuriteNetworkAlias, 10002);
        return builder.ConnectionString;
    }

    private static string RewriteCosmosConnectionString(string connectionString)
    {
        DbConnectionStringBuilder builder = CreateBuilder(connectionString);
        builder["AccountEndpoint"] = RewriteEndpoint((string)builder["AccountEndpoint"], DockerAzureEnvironment.CosmosDbNetworkAlias, 8081);
        return builder.ConnectionString;
    }

    private static string RewriteServiceBusConnectionString(string connectionString)
    {
        DbConnectionStringBuilder builder = CreateBuilder(connectionString);
        builder["Endpoint"] = RewriteEndpoint((string)builder["Endpoint"], DockerAzureEnvironment.ServiceBusNetworkAlias, null);
        return builder.ConnectionString;
    }

    private static DbConnectionStringBuilder CreateBuilder(string connectionString)
    {
        DbConnectionStringBuilder builder = new()
        {
            ConnectionString = connectionString,
        };
        return builder;
    }

    private static string RewriteEndpoint(string endpoint, string host, int? port)
    {
        UriBuilder uriBuilder = new(endpoint)
        {
            Host = host,
        };

        if (port.HasValue)
            uriBuilder.Port = port.Value;

        return uriBuilder.Uri.ToString();
    }

    private static async Task WaitForHttpReadyAsync(string baseUrl, CancellationToken cancellationToken)
    {
        using HttpClient client = new() { BaseAddress = new Uri(baseUrl) };
        DateTime deadline = DateTime.UtcNow.AddMinutes(2);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using HttpResponseMessage response = await client.GetAsync(string.Empty, cancellationToken).ConfigureAwait(false);
                if ((int)response.StatusCode >= 100)
                    return;
            }
            catch
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"The Function App host at '{baseUrl}' did not become reachable within two minutes.");
    }

    private sealed record FunctionAppLocation(string ProjectDirectory, string OutputDirectory);
}