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

internal sealed class FunctionAppEnvComponent : DockerAzureEnvComponent
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
        DockerAzureEnvironment dockerEnvironment = GetDockerEnvironment(environment);
        if (dockerEnvironment.UsedFunctionAppIdentifiers.Count == 0)
            return Array.Empty<IContainer>();

        ConfigStore<FunctionAppConfig>? functionStore = EnvComponentConfigStoreGuard.GetRequiredStore<FunctionAppConfig>(serviceProvider, dockerEnvironment.UsedFunctionAppIdentifiers, "Function App environment setup");
        INetwork network = dockerEnvironment.GetRequiredRuntimeState<INetwork>(DockerAzureEnvironment.NetworkComponentId);
        List<IContainer> containers = [];

        foreach (string identifier in dockerEnvironment.UsedFunctionAppIdentifiers)
        {
            FunctionAppDefinitionDescriptor descriptor = dockerEnvironment.GetRequiredFunctionAppDescriptor(identifier);
            DockerFunctionAppRegistration registration = descriptor.Registration;

            FunctionAppLocation location = ResolveFunctionAppLocation(registration.FunctionType);
            Dictionary<string, string> appSettings = BuildAppSettings(serviceProvider, descriptor, logger);
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
                await LogContainerOutputAsync(container, logger, cancellationToken).ConfigureAwait(false);
                await container.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task LogContainerOutputAsync(IContainer container, ScopedLogger logger, CancellationToken cancellationToken)
    {
        try
        {
            (string stdout, string stderr) = await container.GetLogsAsync(
                since: DateTime.UnixEpoch,
                until: DateTime.UtcNow,
                timestampsEnabled: false,
                ct: cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(stdout))
                logger.LogInformation($"Function App container stdout ({container.Id}):{Environment.NewLine}{stdout}");

            if (!string.IsNullOrWhiteSpace(stderr))
                logger.LogWarning($"Function App container stderr ({container.Id}):{Environment.NewLine}{stderr}");
        }
        catch (Exception exception)
        {
            logger.LogWarning($"Failed to capture Function App container logs ({container.Id}): {exception.GetType().Name}: {exception.Message}");
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

    private static Dictionary<string, string> BuildAppSettings(IServiceProvider serviceProvider, FunctionAppDefinitionDescriptor descriptor, ScopedLogger? logger = null)
    {
        DockerFunctionAppRegistration registration = descriptor.Registration;
        Dictionary<string, string> settings = new(StringComparer.OrdinalIgnoreCase)
        { ["AzureWebJobsFeatureFlags"] = "EnableWorkerIndexing" };

        foreach ((string key, string value) in registration.AdditionalSettings)
            settings[key] = value;

        foreach (FunctionAppResourceBinding binding in descriptor.ResourceBindings)
        {
            switch (binding.Kind)
            {
                case FunctionAppResourceBindingKind.Storage:
                    StorageAccountConfig storage = serviceProvider.GetRequiredService<ConfigStore<StorageAccountConfig>>().GetConfig(binding.ResourceIdentifier);
                    string rewrittenStorage = DockerConnectionStringRewriter.RewriteStorageForContainer(storage.ConnectionString);
                    settings[binding.PrimarySettingName] = rewrittenStorage;
                    if (binding.SecondarySettingName is not null)
                        settings[binding.SecondarySettingName] = rewrittenStorage;
                    if (binding.TertiarySettingName is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(storage.TableContainerName))
                            settings[binding.TertiarySettingName] = storage.TableContainerName;
                        else
                            logger?.LogWarning($"Storage identifier '{binding.ResourceIdentifier}' does not define TableContainerName, so Function App setting '{binding.TertiarySettingName}' was not populated.");
                    }
                    break;
                case FunctionAppResourceBindingKind.Cosmos:
                    CosmosContainerDbConfig cosmos = serviceProvider.GetRequiredService<ConfigStore<CosmosContainerDbConfig>>().GetConfig(binding.ResourceIdentifier);
                    settings[binding.PrimarySettingName] = DockerConnectionStringRewriter.RewriteCosmosForContainer(cosmos.ConnectionString);
                    if (binding.SecondarySettingName is not null)
                        settings[binding.SecondarySettingName] = cosmos.DatabaseName;
                    if (binding.TertiarySettingName is not null)
                        settings[binding.TertiarySettingName] = cosmos.ContainerName;
                    break;
                case FunctionAppResourceBindingKind.ServiceBusTrigger:
                    ServiceBusConfig triggerBus = serviceProvider.GetRequiredService<ConfigStore<ServiceBusConfig>>().GetConfig(binding.ResourceIdentifier);
                    settings[binding.PrimarySettingName] = DockerConnectionStringRewriter.RewriteServiceBusForSameNetworkContainer(triggerBus.ConnectionString);
                    if (binding.SecondarySettingName is not null)
                        settings[binding.SecondarySettingName] = triggerBus.TopicName ?? triggerBus.QueueName ?? throw new InvalidOperationException($"Service Bus identifier '{binding.ResourceIdentifier}' does not define a queue or topic name.");
                    if (binding.TertiarySettingName is not null && triggerBus.SubscriptionName is not null)
                        settings[binding.TertiarySettingName] = triggerBus.SubscriptionName;
                    break;
                case FunctionAppResourceBindingKind.ServiceBusReply:
                    ServiceBusConfig replyBus = serviceProvider.GetRequiredService<ConfigStore<ServiceBusConfig>>().GetConfig(binding.ResourceIdentifier);
                    settings[binding.PrimarySettingName] = DockerConnectionStringRewriter.RewriteServiceBusForSameNetworkContainer(replyBus.ConnectionString);
                    if (binding.SecondarySettingName is not null)
                        settings[binding.SecondarySettingName] = replyBus.TopicName ?? replyBus.QueueName ?? throw new InvalidOperationException($"Service Bus identifier '{binding.ResourceIdentifier}' does not define a queue or topic name.");
                    break;
            }
        }

        return settings;
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
            catch (OperationCanceledException)
            {
                throw;
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