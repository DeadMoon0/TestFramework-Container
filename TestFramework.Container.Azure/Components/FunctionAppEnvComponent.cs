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

            ContainerOutput location = ContainerOutputResolver.Resolve(registration.FunctionType, "host.json");
            logger.LogInformation("Function App '{0}' resolved type '{1}' to project '{2}' and output '{3}'.", identifier, registration.FunctionType.FullName ?? registration.FunctionType.Name, location.ProjectDirectory, location.OutputDirectory);
            if (location.UsedFallbackOutput)
                logger.LogInformation("Function App '{0}' used the owning project output. {1}", identifier, location.FallbackReason ?? string.Empty);
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
            await ContainerReadiness.WaitForHttpAsync(new Uri(baseUrl), "admin/host/status", FunctionAppReadyTimeout, $"Function App '{identifier}'", logger, cancellationToken).ConfigureAwait(false);
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
                await ContainerLogCapture.CaptureAsync(container, "Function App", logger, cancellationToken).ConfigureAwait(false);
                await container.DisposeAsync().ConfigureAwait(false);
            }
        }
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

}