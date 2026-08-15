using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Extensions.DependencyInjection;
using TestFramework.Container.Sources;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;
using TestFramework.Core.Exceptions;
using TestFramework.Core.Logging;
using TestFramework.Core.Variables;
using TestFramework.Web.Configuration;

namespace TestFramework.Container.Web.Components;

/// <summary>
/// Runs the declared applications in containers and publishes their addresses.
/// </summary>
internal sealed class ApiEnvComponent : WebEnvComponentBase
{
    public override EnvComponentIdentifier Id => DockerWebEnvironment.ApiComponentId;

    /// <summary>
    /// Applications are never reused across runs.
    /// </summary>
    /// <remarks>
    /// A database is worth keeping warm; an application is not. A reused container would go on
    /// serving the code it started with, so an edit-and-rerun cycle would silently test the previous
    /// build. Databases have reset modes for that problem; a stale binary has no equivalent.
    /// </remarks>
    public override EnvComponentReuseMode ReuseMode => EnvComponentReuseMode.PerRun;

    public override IReadOnlyList<EnvComponentIdentifier> Dependencies =>
    [
        DockerWebEnvironment.NetworkComponentId,
        DockerWebEnvironment.SqlServerComponentId,
        DockerWebEnvironment.StubComponentId,
    ];

    public override async Task<object?> CreateAsync(IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(logger);

        DockerWebEnvironment webEnvironment = GetWebEnvironment(environment);
        IReadOnlyList<DockerApiDefinition> definitions = webEnvironment.GetApiDefinitions();
        if (definitions.Count == 0)
            return null;

        WebConfigStore<ApiConfig> configStore = GetRequiredConfigStore(serviceProvider);
        INetwork network = webEnvironment.GetRequiredRuntimeState<INetwork>(DockerWebEnvironment.NetworkComponentId);

        // Declaration order in, declaration order out, however the starts interleave.
        IReadOnlyList<DockerApiDefinition> ordered = [.. definitions.OrderBy(definition => definition.Identifier.ToString(), StringComparer.Ordinal)];

        // Phase one is serial on purpose. Planning reads the project and building runs 'dotnet publish'
        // or 'docker build'; neither has been shown safe to run concurrently against the same project
        // or the same package cache, and a wrong answer there is a wrong image, not a slow one.
        List<PlannedApi> planned = [];
        foreach (DockerApiDefinition definition in ordered)
        {
            DockerApiSpec spec = definition.Build();
            string identifier = definition.Identifier;

            // The plan says what will happen before it happens, and every value in it was either
            // declared or read from the project. Nothing is inferred from where an assembly sat.
            ContainerSourcePlan plan = await ContainerSourceResolver.PlanAsync(definition.Source, cancellationToken).ConfigureAwait(false);
            plan = await ContainerImageBuilder.BuildAsync(plan, identifier, logger, cancellationToken).ConfigureAwait(false);

            IReadOnlyDictionary<string, string> settings = ComposeSettings(webEnvironment, definition, spec);
            string settingsJson = ApiSettingsFile.Compose(settings);
            string settingsFileName = ApiSettingsFile.FileName(spec.EnvironmentName);

            logger.LogInformation("API '{0}' settings '{1}':{2}{3}", identifier, settingsFileName, Environment.NewLine, settingsJson);

            planned.Add(new PlannedApi(definition, spec, plan, settingsFileName, settingsJson));
        }

        // Phase two races: creating the container, starting it and waiting it out are independent per
        // application, and the readiness waits are what the run actually spends its time on.
        IReadOnlyList<StartedApi> started = await ContainerStartCoordinator.StartAllAsync(
            planned,
            (plan, token) => StartApiAsync(plan, network, logger, token),
            result => result.Container,
            cancellationToken).ConfigureAwait(false);

        List<RunningApi> apis = [];
        foreach (StartedApi api in started)
        {
            Publish(configStore, api.Planned.Definition.Identifier, api.BaseUrl, api.Planned.Spec.HealthPath);
            apis.Add(new RunningApi(api.Planned.Definition.Identifier, api.Container, api.BaseUrl, api.Planned.Plan, api.Planned.SettingsFileName, api.Planned.SettingsJson));
            logger.LogInformation("API '{0}' is reachable at '{1}'.", api.Planned.Definition.Identifier, api.BaseUrl);
        }

        ApiComponentState state = new(apis);
        webEnvironment.SetRuntimeState(Id, state);
        return state;
    }

    public override async Task DeconstructAsync(object? state, IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (state is not ApiComponentState apiState)
            return;

        foreach (RunningApi api in apiState.Apis)
        {
            // The log dies with the container, and an application failure is invisible from the test
            // side without it.
            await ContainerLogCapture.CaptureAsync(api.Container, $"API '{api.Identifier}'", logger, cancellationToken).ConfigureAwait(false);
            await ContainerDockerCommands.ForceRemoveContainerAsync(api.Container, cancellationToken).ConfigureAwait(false);

            // An image the run built is the run's litter. One the caller named is not.
            if (api.Plan.Kind == ContainerSourceKind.Project && api.Plan.Image is { } builtImage)
                await ContainerImageBuilder.RemoveImageAsync(builtImage, cancellationToken).ConfigureAwait(false);

            if (api.Plan.Strategy == ContainerBuildStrategy.HostPublish && api.Plan.OutputDirectory is { } published)
                DeletePublishOutput(published, logger);
        }
    }

    private static async Task<StartedApi> StartApiAsync(PlannedApi planned, INetwork network, ScopedLogger logger, CancellationToken cancellationToken)
    {
        IContainer container = BuildContainer(planned.Spec, planned.Plan, network, planned.SettingsFileName, planned.SettingsJson);
        await StartAsync(container, planned.Definition, planned.Plan.Image ?? "(built from output)", logger, cancellationToken).ConfigureAwait(false);

        Uri baseUrl = ContainerEndpoints.HostEndpoint(container, planned.Spec.InternalPort);
        await WaitForReadinessAsync(container, planned.Definition, planned.Spec, baseUrl, logger, cancellationToken).ConfigureAwait(false);

        return new StartedApi(planned, container, baseUrl);
    }

    private sealed record PlannedApi(DockerApiDefinition Definition, DockerApiSpec Spec, ContainerSourcePlan Plan, string SettingsFileName, string SettingsJson);

    private sealed record StartedApi(PlannedApi Planned, IContainer Container, Uri BaseUrl);

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

    private static IReadOnlyDictionary<string, string> ComposeSettings(DockerWebEnvironment webEnvironment, DockerApiDefinition definition, DockerApiSpec spec)
    {
        Dictionary<string, string> settings = new(spec.Settings, StringComparer.OrdinalIgnoreCase);

        // The application is on the network, so every address it is given is a network address.
        // Handing it a host-mapped one would work from the test process and fail from inside the
        // container.
        if (spec.SqlBindings.Count > 0)
        {
            SqlServerComponentState sqlState = webEnvironment.GetRequiredRuntimeState<SqlServerComponentState>(DockerWebEnvironment.SqlServerComponentId);
            foreach (DockerApiSqlBinding binding in spec.SqlBindings)
                settings[binding.SettingPath] = sqlState.GetRequiredDatabase(binding.SqlIdentifier).NetworkConnectionString;
        }

        if (spec.StubBindings.Count > 0)
        {
            StubComponentState stubState = webEnvironment.GetRequiredRuntimeState<StubComponentState>(DockerWebEnvironment.StubComponentId);
            foreach (DockerApiStubBinding binding in spec.StubBindings)
                settings[binding.SettingPath] = stubState.GetRequiredStub(binding.StubIdentifier).NetworkBaseUrl.ToString();
        }

        return settings;
    }

    private static IContainer BuildContainer(
        DockerApiSpec spec,
        ContainerSourcePlan plan,
        INetwork network,
        string settingsFileName,
        string settingsJson)
    {
        string port = spec.InternalPort.ToString(CultureInfo.InvariantCulture);
        string image = plan.Image ?? spec.ResolveImage(plan.TargetFramework ?? throw new FrameworkStateException("The plan has neither an image nor a target framework to choose one from."));

        ContainerBuilder builder = new ContainerBuilder(image)
            .WithNetwork(network)
            .WithPortBinding(spec.InternalPort, true)
            .WithCreateParameterModifier(ContainerPortBinding.Apply)
            .WithWorkingDirectory(DockerWebDefaults.ApiRoot)
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", spec.EnvironmentName)
            .WithEnvironment("DOTNET_ENVIRONMENT", spec.EnvironmentName)
            .WithEnvironment("ASPNETCORE_HTTP_PORTS", port)
            // Container paths are always separated by '/', whatever the host does.
            .WithResourceMapping(ApiSettingsFile.ToBytes(settingsJson), $"{DockerWebDefaults.ApiRoot}/{settingsFileName}");

        // An image already knows how to start itself. A directory has to be put somewhere and given
        // a command; it is copied rather than bind-mounted, so the generated settings file can sit
        // beside it without anything being written back into the project's own output.
        if (plan.Image is null)
        {
            builder = builder
                .WithResourceMapping(new DirectoryInfo(plan.OutputDirectory!), DockerWebDefaults.ApiRoot)
                .WithCommand("dotnet", $"{DockerWebDefaults.ApiRoot}/{plan.AssemblyFileName}");
        }

        foreach ((string name, string value) in spec.EnvironmentVariables)
            builder = builder.WithEnvironment(name, value);

        return builder.Build();
    }

    private static async Task StartAsync(IContainer container, DockerApiDefinition definition, string image, ScopedLogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await container.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await ContainerLogCapture.CaptureAsync(container, $"API '{definition.Identifier}'", logger, cancellationToken).ConfigureAwait(false);
            throw new FrameworkStateException(
                $"The container for API '{definition.Identifier}' did not start on image '{image}'.",
                [
                    "Read the captured container log in the run output; it holds the application's own startup error.",
                    "Verify the application was built for a framework the runtime image can run.",
                ],
                null,
                exception);
        }
    }

    private static async Task WaitForReadinessAsync(
        IContainer container,
        DockerApiDefinition definition,
        DockerApiSpec spec,
        Uri baseUrl,
        ScopedLogger logger,
        CancellationToken cancellationToken)
    {
        string description = $"API '{definition.Identifier}'";

        try
        {
            if (spec.HealthPath is { } healthPath)
                await ContainerReadiness.WaitForHttpAsync(baseUrl, healthPath, spec.ReadinessTimeout, description, logger, cancellationToken).ConfigureAwait(false);
            else
                await ContainerReadiness.WaitForHttpAnswerAsync(baseUrl, "/", spec.ReadinessTimeout, description, logger, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Capturing before rethrowing is the difference between a bare timeout and a stack trace
            // from inside the application.
            await ContainerLogCapture.CaptureAsync(container, description, logger, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static WebConfigStore<ApiConfig> GetRequiredConfigStore(IServiceProvider serviceProvider)
        => serviceProvider.GetService<WebConfigStore<ApiConfig>>()
        ?? throw new FrameworkConfigurationException(
            "The run has no API configuration store, so the container has nowhere to publish its address. "
            + "Call LoadWebConfig() on the config instance the run is set up with.");

    private static void Publish(WebConfigStore<ApiConfig> configStore, string identifier, Uri baseUrl, string? healthPath)
    {
        if (configStore.TryGetConfig(identifier, out ApiConfig? existing) && existing is not null)
        {
            configStore.AddConfig(identifier, existing with
            {
                BaseUrl = baseUrl.ToString(),
                HealthPath = healthPath ?? existing.HealthPath,
            });

            return;
        }

        configStore.AddConfig(identifier, new ApiConfig
        {
            BaseUrl = baseUrl.ToString(),
            HealthPath = healthPath ?? DockerWebDefaults.ApiHealthPath,
        });
    }
}
