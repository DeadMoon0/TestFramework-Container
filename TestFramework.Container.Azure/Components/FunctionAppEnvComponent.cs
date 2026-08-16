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
using TestFramework.Container.Sources;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Exceptions;
using TestFramework.Core.Environment;
using TestFramework.Core.Logging;
using TestFramework.Core.Variables;

namespace TestFramework.Container.Azure.Components;

internal sealed class FunctionAppEnvComponent(DockerAzureEnvironment owner) : DockerAzureEnvComponent
{
    private const string FunctionAppRoot = "/home/site/wwwroot";
    private static readonly TimeSpan FunctionAppReadyTimeout = TimeSpan.FromMinutes(4);

    public override EnvComponentIdentifier Id => DockerAzureEnvironment.FunctionAppComponentId;

    /// <summary>
    /// The emulators the resolved Function Apps actually bind to, plus the network.
    /// </summary>
    /// <remarks>
    /// This property reads resolution state and must not be consulted before
    /// <see cref="DockerAzureEnvironment.ResolveComponents"/> has run: it then degrades to the network component alone.
    /// That is safe because <see cref="CreateAsync"/> early-returns when no Function App resolved.
    /// </remarks>
    public override IReadOnlyList<EnvComponentIdentifier> Dependencies => owner.GetFunctionAppComponentDependencies();

    public override async Task<object?> CreateAsync(IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        DockerAzureEnvironment dockerEnvironment = GetDockerEnvironment(environment);
        if (dockerEnvironment.UsedFunctionAppIdentifiers.Count == 0)
            return new FunctionAppComponentState([]);

        ConfigStore<FunctionAppConfig>? functionStore = EnvComponentConfigStoreGuard.GetRequiredStore<FunctionAppConfig>(dockerEnvironment, serviceProvider, dockerEnvironment.UsedFunctionAppIdentifiers, "Function App environment setup");
        INetwork network = dockerEnvironment.GetRequiredRuntimeState<INetwork>(DockerAzureEnvironment.NetworkComponentId);
        DockerEndpointMap endpointMap = dockerEnvironment.GetEndpointMap();

        dockerEnvironment.LogPendingResolutionSummary(logger);

        // Preparation stays serial: it reads and seeds config stores, which are guarded on creation but
        // not on access, and it costs nothing next to a container start.
        List<PlannedFunctionApp> planned = [];
        foreach (string identifier in dockerEnvironment.UsedFunctionAppIdentifiers.OrderBy(x => x, StringComparer.Ordinal))
            planned.Add(await PrepareFunctionAppAsync(dockerEnvironment, serviceProvider, identifier, logger, cancellationToken).ConfigureAwait(false));

        IReadOnlyList<StartedFunctionApp> started = await ContainerStartCoordinator.StartAllAsync(
            planned,
            (plan, token) => StartFunctionAppAsync(plan, network, endpointMap, logger, token),
            result => result.Container,
            cancellationToken).ConfigureAwait(false);

        // Publishing is a config-store write, so it happens once every start has settled.
        foreach (StartedFunctionApp app in started)
        {
            FunctionAppConfig current = functionStore!.GetConfig(app.Identifier);
            functionStore.AddConfig(app.Identifier, current with { BaseUrl = app.BaseUrl });
        }

        return new FunctionAppComponentState(started);
    }

    private static async Task<PlannedFunctionApp> PrepareFunctionAppAsync(
        DockerAzureEnvironment dockerEnvironment,
        IServiceProvider serviceProvider,
        string identifier,
        ScopedLogger logger,
        CancellationToken cancellationToken)
    {
        FunctionAppDefinitionDescriptor descriptor = dockerEnvironment.GetRequiredFunctionAppDescriptor(identifier);
        DockerFunctionAppRegistration registration = descriptor.Registration;

        // The plan states where the payload comes from, and names the derivation behind every value it
        // worked out, before anything is published or mounted.
        ContainerSourcePlan plan = await ContainerSourceResolver.PlanAsync(registration.Source, cancellationToken).ConfigureAwait(false);
        EnsureMountable(plan, identifier);
        plan = await ContainerImageBuilder.BuildAsync(plan, identifier, logger, cancellationToken).ConfigureAwait(false);

        string payloadDirectory = plan.OutputDirectory
            ?? throw new FrameworkConfigurationException($"The source of Function App '{identifier}' ({registration.DescribeSource()}) produced no directory to mount into the Functions host.");

        EnsureFunctionAppPayload(payloadDirectory, identifier);

        string image = ResolveHostImage(registration, plan, identifier, logger);

        Dictionary<string, string> appSettings = BuildAppSettings(dockerEnvironment, serviceProvider, descriptor, logger);
        appSettings["AzureFunctionsJobHost__Logging__Console__IsEnabled"] = "true";
        appSettings["AzureWebJobsScriptRoot"] = FunctionAppRoot;
        appSettings["FUNCTIONS_WORKER_RUNTIME"] = "dotnet-isolated";
        appSettings["ASPNETCORE_URLS"] = "http://0.0.0.0:80";
        appSettings["PORT"] = "80";
        appSettings["WEBSITES_PORT"] = "80";
        logger.LogInformation("Function App '{0}' app settings keys: {1}", identifier, string.Join(", ", appSettings.Keys.OrderBy(x => x, StringComparer.Ordinal)));

        return new PlannedFunctionApp(identifier, image, payloadDirectory, appSettings, plan);
    }

    /// <summary>
    /// Chooses the Functions host image that can actually run this payload.
    /// </summary>
    /// <param name="registration">The registration, which may have named an image itself.</param>
    /// <param name="plan">The carried-out source plan, which knows what the payload was built for.</param>
    /// <param name="identifier">The Function App identifier, for log and error output.</param>
    /// <param name="logger">The scoped logger.</param>
    /// <exception cref="FrameworkConfigurationException">A declared image cannot run the payload.</exception>
    /// <remarks>
    /// A Functions host image carries exactly one .NET runtime. Mounting an application built for a
    /// different one produces no useful failure: the container starts, the host starts, the worker
    /// exits with code 150, and the host then sits there not answering until the readiness timeout
    /// expires -- with the real reason ("To install missing framework...") visible only inside the
    /// container log. So the framework is matched here, before anything starts.
    ///
    /// An image the caller named is never overridden; it is only checked, because a caller naming a
    /// private or pinned image knows things this does not. What is checked is the one thing that can
    /// be read off both: the runtime version.
    /// </remarks>
    private static string ResolveHostImage(
        DockerFunctionAppRegistration registration,
        ContainerSourcePlan plan,
        string identifier,
        ScopedLogger logger)
    {
        if (plan.TargetFramework is not { Length: > 0 } targetFramework)
            return registration.Image;

        string? matching = DockerAzureDefaults.FunctionAppImageFor(targetFramework);
        if (matching is null)
            return registration.Image;

        if (!registration.ImageWasDeclared)
        {
            if (!string.Equals(matching, registration.Image, StringComparison.Ordinal))
                logger.LogInformation("Function App '{0}' runs on '{1}', matched to the '{2}' its payload was built for.", identifier, matching, targetFramework);

            return matching;
        }

        // Declared, so it stands -- but a version that cannot run the payload is worth saying now
        // rather than letting it surface as four minutes of silence.
        if (!registration.Image.EndsWith($"-dotnet-isolated{targetFramework[3..]}", StringComparison.OrdinalIgnoreCase)
            && registration.Image.StartsWith(DockerAzureDefaults.FunctionAppImageRepository, StringComparison.OrdinalIgnoreCase))
        {
            throw new FrameworkConfigurationException(
                $"Function App '{identifier}' is built for '{targetFramework}', and the host image '{registration.Image}' carries a different .NET runtime.",
                [
                    $"Use '{matching}', which carries the runtime this payload needs.",
                    "Or build the Function App for the framework the declared image carries.",
                    "A Functions host image bundles one runtime: a mismatch starts the container, fails the worker with exit code 150, and then answers nothing until the readiness timeout expires.",
                ]);
        }

        return registration.Image;
    }

    /// <summary>
    /// Rejects a source whose result could not be mounted into the Functions host.
    /// </summary>
    /// <remarks>
    /// A Function App is not an application that starts itself. The Functions host image is the thing
    /// that runs, and the application is mounted into it at <c>/home/site/wwwroot</c>. A strategy that
    /// produces an image rather than a directory therefore has nothing this component can use, and a
    /// source that names an existing image has the same problem.
    /// </remarks>
    private static void EnsureMountable(ContainerSourcePlan plan, string identifier)
    {
        if (plan.Kind == ContainerSourceKind.Image)
        {
            throw new FrameworkConfigurationException(
                $"Function App '{identifier}' declares an image source, and a Function App payload is mounted into the Functions host image rather than run as one.",
                [
                    "Declare the payload with ContainerSource.Project(\"...\").BuiltOnHost() or ContainerSource.Directory(\"...\").",
                    "To change the Functions host image itself, override Image on the definition.",
                ]);
        }

        if (plan.Kind == ContainerSourceKind.Project && plan.Strategy != ContainerBuildStrategy.HostPublish)
        {
            throw new FrameworkConfigurationException(
                $"Function App '{identifier}' builds its project with '{plan.Strategy}', which produces an image, and a Function App payload has to be a directory to mount into the Functions host.",
                ["Call BuiltOnHost() on the source, which publishes to a directory."]);
        }
    }

    /// <summary>
    /// Refuses to mount a directory the Functions host would reject.
    /// </summary>
    /// <remarks>
    /// A host started on a payload with no <c>host.json</c> comes up and then reports no functions at
    /// all, which reads as a broken test rather than a missing file.
    /// </remarks>
    private static void EnsureFunctionAppPayload(string payloadDirectory, string identifier)
    {
        if (File.Exists(Path.Combine(payloadDirectory, "host.json")))
            return;

        throw new FrameworkConfigurationException(
            $"The payload for Function App '{identifier}' at '{payloadDirectory}' has no host.json, so the Functions host would start with no functions.",
            [
                "Check that the project is an Azure Functions project and that host.json is copied to the output.",
                "Build or publish the project before starting the container environment.",
            ]);
    }

    private static async Task<StartedFunctionApp> StartFunctionAppAsync(
        PlannedFunctionApp plan,
        INetwork network,
        DockerEndpointMap endpointMap,
        ScopedLogger logger,
        CancellationToken cancellationToken)
    {
        ContainerBuilder builder = new ContainerBuilder(plan.Image)
            .WithNetwork(network)
            .WithPortBinding(80, true)
            .WithCreateParameterModifier(ContainerPortBinding.Apply)
            .WithBindMount(plan.PayloadDirectory, FunctionAppRoot, AccessMode.ReadOnly);

        foreach ((string key, string value) in plan.AppSettings)
            builder = builder.WithEnvironment(key, value);

        IContainer container = builder.Build();
        logger.LogInformation("Function App '{0}' starting image '{1}' with mount '{2}' -> '{3}'.", plan.Identifier, plan.Image, plan.PayloadDirectory, FunctionAppRoot);

        await container.StartAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Function App '{0}' container '{1}' started. Waiting for host readiness.", plan.Identifier, container.Id);

        string baseUrl = endpointMap.GetFunctionAppBaseUrl(container);
        await ContainerReadiness.WaitForHttpAsync(new Uri(baseUrl), "admin/host/status", FunctionAppReadyTimeout, $"Function App '{plan.Identifier}'", logger, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Function App '{0}' is reachable at '{1}'.", plan.Identifier, baseUrl);

        return new StartedFunctionApp(plan.Identifier, container, baseUrl, plan.PayloadDirectory, plan.Plan);
    }

    private sealed record PlannedFunctionApp(string Identifier, string Image, string PayloadDirectory, IReadOnlyDictionary<string, string> AppSettings, ContainerSourcePlan Plan);

    private sealed record StartedFunctionApp(string Identifier, IContainer Container, string BaseUrl, string PayloadDirectory, ContainerSourcePlan Plan);

    private sealed record FunctionAppComponentState(IReadOnlyList<StartedFunctionApp> Apps);

    public override async Task DeconstructAsync(object? state, IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        if (state is not FunctionAppComponentState functionAppState)
            return;

        foreach (StartedFunctionApp app in functionAppState.Apps)
        {
            await ContainerLogCapture.CaptureAsync(app.Container, $"Function App '{app.Identifier}'", logger, cancellationToken).ConfigureAwait(false);
            await app.Container.DisposeAsync().ConfigureAwait(false);

            // A publish this run made into the temp directory is the run's litter. A directory the
            // caller named, or a project's own build output, is not.
            if (app.Plan.Kind == ContainerSourceKind.Project && app.Plan.Strategy == ContainerBuildStrategy.HostPublish)
                DeletePublishOutput(app.PayloadDirectory, logger);
        }
    }

    private static void DeletePublishOutput(string directory, ScopedLogger logger)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A temporary directory left behind is untidy, never a reason to fail a run.
            logger.LogWarning($"The published output '{directory}' could not be removed: {exception.Message}");
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