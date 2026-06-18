using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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
    private static readonly TimeSpan FunctionAppReadyTimeout = TimeSpan.FromMinutes(4);

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

        ConfigStore<FunctionAppConfig>? functionStore = EnvComponentConfigStoreGuard.GetRequiredStore<FunctionAppConfig>(dockerEnvironment, serviceProvider, dockerEnvironment.UsedFunctionAppIdentifiers, "Function App environment setup");
        INetwork network = dockerEnvironment.GetRequiredRuntimeState<INetwork>(DockerAzureEnvironment.NetworkComponentId);
        DockerEndpointMap endpointMap = dockerEnvironment.GetEndpointMap();
        List<IContainer> containers = [];

        dockerEnvironment.LogPendingResolutionSummary(logger);

        foreach (string identifier in dockerEnvironment.UsedFunctionAppIdentifiers)
        {
            FunctionAppDefinitionDescriptor descriptor = dockerEnvironment.GetRequiredFunctionAppDescriptor(identifier);
            DockerFunctionAppRegistration registration = descriptor.Registration;

            FunctionAppLocation location = ResolveFunctionAppLocation(registration.FunctionType);
            logger.LogInformation("Function App '{0}' resolved type '{1}' to project '{2}' and output '{3}'.", identifier, registration.FunctionType.FullName ?? registration.FunctionType.Name, location.ProjectDirectory, location.OutputDirectory);
            Dictionary<string, string> appSettings = BuildAppSettings(dockerEnvironment, serviceProvider, descriptor, logger);
            appSettings["AzureFunctionsJobHost__Logging__Console__IsEnabled"] = "true";
            appSettings["AzureWebJobsScriptRoot"] = FunctionAppRoot;
            appSettings["FUNCTIONS_WORKER_RUNTIME"] = "dotnet-isolated";
            appSettings["ASPNETCORE_URLS"] = "http://0.0.0.0:80";
            appSettings["PORT"] = "80";
            appSettings["WEBSITES_PORT"] = "80";
            logger.LogInformation("Function App '{0}' app settings keys: {1}", identifier, string.Join(", ", appSettings.Keys.OrderBy(x => x, StringComparer.Ordinal)));

            ContainerBuilder builder = new ContainerBuilder(registration.Image)
                .WithNetwork(network)
                .WithPortBinding(80, true)
                .WithBindMount(location.OutputDirectory, FunctionAppRoot, AccessMode.ReadOnly);
            logger.LogInformation("Function App '{0}' starting image '{1}' with mount '{2}' -> '{3}'.", identifier, registration.Image, location.OutputDirectory, FunctionAppRoot);

            foreach ((string key, string value) in appSettings)
                builder = builder.WithEnvironment(key, value);

            IContainer container = builder.Build();

            await container.StartAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Function App '{0}' container '{1}' started. Waiting for host readiness.", identifier, container.Id);

            string baseUrl = endpointMap.GetFunctionAppBaseUrl(container);
            await WaitForHttpReadyAsync(identifier, baseUrl, logger, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Function App '{0}' is reachable at '{1}'.", identifier, baseUrl);

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
        string? fallbackOutputDirectory = null;
        if (!LooksLikeFunctionAppOutput(outputDirectory, assemblyName))
        {
            string configuration = ResolveBuildConfiguration(outputDirectory);
            fallbackOutputDirectory = Path.Combine(projectDirectory, "bin", configuration, "net8.0");
            if (LooksLikeFunctionAppOutput(fallbackOutputDirectory, assemblyName))
                outputDirectory = fallbackOutputDirectory;
        }

        if (!LooksLikeFunctionAppOutput(outputDirectory, assemblyName))
            throw CreateMissingFunctionAppOutputException(functionType, assemblyName, projectDirectory, outputDirectory, fallbackOutputDirectory);

        return new FunctionAppLocation(projectDirectory, outputDirectory);
    }

    private static bool LooksLikeFunctionAppOutput(string outputDirectory, string assemblyName)
        => File.Exists(Path.Combine(outputDirectory, "host.json"))
        && File.Exists(Path.Combine(outputDirectory, $"{assemblyName}.dll"));

    private static string ResolveBuildConfiguration(string outputDirectory)
    {
        if (outputDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            return "Release";

        if (outputDirectory.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            return "Debug";

        return "Debug";
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

    private static Dictionary<string, string> BuildAppSettings(DockerAzureEnvironment dockerEnvironment, IServiceProvider serviceProvider, FunctionAppDefinitionDescriptor descriptor, ScopedLogger? logger = null)
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
                    StorageAccountConfig storage = dockerEnvironment.GetOrCreateConfigStore<StorageAccountConfig>(serviceProvider, [binding.ResourceIdentifier], "Function App environment setup")!.GetConfig(binding.ResourceIdentifier);
                    string rewrittenStorage = dockerEnvironment.GetEndpointMap().RewriteStorageForContainer(storage.ConnectionString);
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
                    CosmosContainerDbConfig cosmos = dockerEnvironment.GetOrCreateConfigStore<CosmosContainerDbConfig>(serviceProvider, [binding.ResourceIdentifier], "Function App environment setup")!.GetConfig(binding.ResourceIdentifier);
                    settings[binding.PrimarySettingName] = dockerEnvironment.GetEndpointMap().RewriteCosmosForContainer(cosmos.ConnectionString);
                    if (binding.SecondarySettingName is not null)
                        settings[binding.SecondarySettingName] = cosmos.DatabaseName;
                    if (binding.TertiarySettingName is not null)
                        settings[binding.TertiarySettingName] = cosmos.ContainerName;
                    break;
                case FunctionAppResourceBindingKind.ServiceBusTrigger:
                    ServiceBusConfig triggerBus = dockerEnvironment.GetOrCreateConfigStore<ServiceBusConfig>(serviceProvider, [binding.ResourceIdentifier], "Function App environment setup")!.GetConfig(binding.ResourceIdentifier);
                    settings[binding.PrimarySettingName] = dockerEnvironment.GetEndpointMap().RewriteServiceBusForContainer(triggerBus.ConnectionString);
                    if (binding.ServiceBusEndpoint is { } triggerEndpoint)
                    {
                        if (binding.SecondarySettingName is not null)
                            settings[binding.SecondarySettingName] = triggerEndpoint.EntityName;
                        if (binding.TertiarySettingName is not null && triggerEndpoint.SubscriptionName is not null)
                            settings[binding.TertiarySettingName] = triggerEndpoint.SubscriptionName;
                    }
                    break;
                case FunctionAppResourceBindingKind.ServiceBusReply:
                    ServiceBusConfig replyBus = dockerEnvironment.GetOrCreateConfigStore<ServiceBusConfig>(serviceProvider, [binding.ResourceIdentifier], "Function App environment setup")!.GetConfig(binding.ResourceIdentifier);
                    settings[binding.PrimarySettingName] = dockerEnvironment.GetEndpointMap().RewriteServiceBusForContainer(replyBus.ConnectionString);
                    if (binding.ServiceBusEndpoint is { } replyEndpoint && binding.SecondarySettingName is not null)
                        settings[binding.SecondarySettingName] = replyEndpoint.EntityName;
                    break;
            }
        }

        return settings;
    }

    private static InvalidOperationException CreateMissingFunctionAppOutputException(Type functionType, string assemblyName, string projectDirectory, string selectedOutputDirectory, string? fallbackOutputDirectory)
    {
        List<string> details =
        [
            $"Function App type '{functionType.FullName}' must resolve to build output before the Docker container can start.",
            $"Project directory: {projectDirectory}",
            $"Selected output directory: {selectedOutputDirectory}",
            $"Expected files: {Path.Combine(selectedOutputDirectory, "host.json")} and {Path.Combine(selectedOutputDirectory, $"{assemblyName}.dll")}",
        ];

        if (!string.IsNullOrWhiteSpace(fallbackOutputDirectory) && !string.Equals(fallbackOutputDirectory, selectedOutputDirectory, StringComparison.OrdinalIgnoreCase))
            details.Add($"Checked fallback output directory: {fallbackOutputDirectory}");

        details.Add("Build or publish the Function App project before starting the Docker Azure environment.");
        return new InvalidOperationException(string.Join(Environment.NewLine, details));
    }

    private static async Task WaitForHttpReadyAsync(string identifier, string baseUrl, ScopedLogger logger, CancellationToken cancellationToken)
    {
        using HttpClient client = new() { BaseAddress = new Uri(baseUrl) };
        DateTime deadline = DateTime.UtcNow.Add(FunctionAppReadyTimeout);
        logger.LogInformation("Function App '{0}' waiting up to {1} for admin/host/status at '{2}'.", identifier, FunctionAppReadyTimeout, new Uri(client.BaseAddress!, "admin/host/status"));

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using HttpResponseMessage response = await client.GetAsync("admin/host/status", cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
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

        throw new TimeoutException($"The Function App host for '{identifier}' at '{baseUrl}' did not become reachable within {FunctionAppReadyTimeout.TotalMinutes:0} minutes.");
    }

    private sealed record FunctionAppLocation(string ProjectDirectory, string OutputDirectory);
}