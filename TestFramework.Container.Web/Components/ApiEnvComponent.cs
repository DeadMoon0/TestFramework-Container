using TestFramework.Core.Environment.Graph;
using System.Text;
using TestFramework.Core.Steps;
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
using TestFramework.Web;
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

    public override async Task<object?> CreateAsync(IEnvironmentProvider environment, RunContext context)
    {
        ArgumentNullException.ThrowIfNull(context.Services);
        ArgumentNullException.ThrowIfNull(context.Logger);

        DockerWebEnvironment webEnvironment = GetWebEnvironment(environment);
        IReadOnlyList<DockerApiDefinition> definitions = webEnvironment.GetApiDefinitions();
        if (definitions.Count == 0)
            return null;

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
            ContainerSourcePlan plan = await ContainerSourceResolver.PlanAsync(definition.Source, context.Deadline.Token).ConfigureAwait(false);
            plan = await ContainerImageBuilder.BuildAsync(plan, identifier, context.Logger, context.Deadline.Token).ConfigureAwait(false);

            string image = ResolveRunImage(definition, spec, plan);

            // Recorded on the run because nobody stated it: for a built or derived image, a run that
            // passed could not otherwise say which image proved it. §5's third demand.
            context.EffectiveSettings.Record(ImageSource, $"api:{identifier}:Image", image);

            IReadOnlyDictionary<string, string> settings = ComposeSettings(webEnvironment, definition, spec);
            string settingsFileName = ApiSettingsFile.FileName(spec.EnvironmentName);
            string settingsJson = new JsonPathDocument(settingsFileName).Compose(settings, existing: null);

            // Keys only: the values carry the database connection strings, credentials included, and a
            // value the store redacts in every listing must not reappear through a log line. The full
            // document stays readable on the component state for the test that needs it.
            context.Logger.LogInformation("API '{0}' settings '{1}' keys: {2}", identifier, settingsFileName, string.Join(", ", settings.Keys.OrderBy(x => x, StringComparer.Ordinal)));

            planned.Add(new PlannedApi(definition, spec, plan, image, settingsFileName, settingsJson));
        }

        // Phase two races: creating the container, starting it and waiting it out are independent per
        // application, and the readiness waits are what the run actually spends its time on.
        IReadOnlyList<StartedApi> started = await ContainerStartCoordinator.StartAllAsync(
            planned,
            (plan, token) => StartApiAsync(plan, network, context.Logger, token),
            result => result.Container,
            context.Deadline.Token).ConfigureAwait(false);

        List<RunningApi> apis = [];
        foreach (StartedApi api in started)
        {
            Publish(context, api);
            apis.Add(new RunningApi(api.Planned.Definition.Identifier, api.Container, api.BaseUrl, api.NetworkBaseUrl, api.Planned.Plan, api.Planned.SettingsFileName, api.Planned.SettingsJson));
            context.Logger.LogInformation("API '{0}' is reachable at '{1}'.", api.Planned.Definition.Identifier, api.BaseUrl);
        }

        ApiComponentState state = new(apis);
        webEnvironment.SetRuntimeState(Id, state);
        return state;
    }

    public override async Task DeconstructAsync(object? state, IEnvironmentProvider environment, RunContext context)
    {
        ArgumentNullException.ThrowIfNull(context.Logger);

        if (state is not ApiComponentState apiState)
            return;

        foreach (RunningApi api in apiState.Apis)
        {
            // The log dies with the container, and an application failure is invisible from the test
            // side without it.
            await ContainerLogCapture.CaptureAsync(api.Container, $"API '{api.Identifier}'", context.Logger, context.Deadline.Token).ConfigureAwait(false);
            await ContainerDockerCommands.ForceRemoveContainerAsync(api.Container, context.Deadline.Token).ConfigureAwait(false);

            // An image the run built is the run's litter. One the caller named is not.
            if (api.Plan.Kind == ContainerSourceKind.Project && api.Plan.Image is { } builtImage)
                await ContainerImageBuilder.RemoveImageAsync(builtImage, context.Deadline.Token).ConfigureAwait(false);

            if (api.Plan.Strategy == ContainerBuildStrategy.HostPublish && api.Plan.OutputDirectory is { } published)
                DeletePublishOutput(published, context.Logger);
        }
    }

    /// <summary>
    /// The alias other containers on the environment's network reach an application by.
    /// </summary>
    /// <param name="identifier">The API identifier.</param>
    internal static string NetworkAlias(string identifier) => $"api-{identifier}";

    private static async Task<StartedApi> StartApiAsync(PlannedApi planned, INetwork network, ScopedLogger logger, CancellationToken cancellationToken)
    {
        string alias = NetworkAlias(planned.Definition.Identifier);
        IContainer container = BuildContainer(planned, network, alias);
        await StartAsync(container, planned.Definition, planned.Image, logger, cancellationToken).ConfigureAwait(false);

        Uri baseUrl = ContainerEndpoints.HostEndpoint(container, planned.Spec.InternalPort);
        Uri networkBaseUrl = ContainerEndpoints.NetworkEndpoint(alias, planned.Spec.InternalPort);
        await WaitForReadinessAsync(container, planned.Definition, planned.Spec, baseUrl, logger, cancellationToken).ConfigureAwait(false);

        return new StartedApi(planned, container, baseUrl, networkBaseUrl);
    }

    private sealed record PlannedApi(DockerApiDefinition Definition, DockerApiSpec Spec, ContainerSourcePlan Plan, string Image, string SettingsFileName, string SettingsJson);

    private sealed record StartedApi(PlannedApi Planned, IContainer Container, Uri BaseUrl, Uri NetworkBaseUrl);

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

    /// <summary>
    /// The one decision of what image an application runs on, made before anything starts.
    /// </summary>
    /// <remarks>
    /// There used to be two deciders: the source plan resolved a runtime image (honouring
    /// <c>WithRuntimeImage</c> and the project's SDK kind) and this component then derived its own from
    /// the target framework, so a declared override was logged in the plan and silently dropped at run
    /// time. Now the plan's answer is the answer, an API-level <c>WithImage</c> beats a derived one and
    /// contradicts a declared one out loud, and a source with no way to choose refuses here - before a
    /// container is built - rather than mid-start.
    /// </remarks>
    private static string ResolveRunImage(DockerApiDefinition definition, DockerApiSpec spec, ContainerSourcePlan plan)
    {
        string? declaredOnApi = string.IsNullOrWhiteSpace(spec.Image) ? null : spec.Image;

        if (plan.Image is { } producedImage)
        {
            if (declaredOnApi is not null && !string.Equals(declaredOnApi, producedImage, StringComparison.Ordinal))
            {
                throw new FrameworkConfigurationException(
                    $"'{definition.GetType().Name}' declares the image '{declaredOnApi}', but its source already produces the image that runs ('{producedImage}'), so one of the two would be ignored.",
                    ["Remove WithImage(...), or declare the image as the source with ContainerSource.Image(...)."]);
            }

            return producedImage;
        }

        if (plan.AssemblyFileName is null)
        {
            throw new FrameworkConfigurationException(
                $"The source of API '{definition.Identifier}' ('{plan.OutputDirectory}') holds no runnable application, so there is nothing to start.",
                ["Ship a directory holding a published application, or declare the project with ContainerSource.Project(...)."]);
        }

        if (declaredOnApi is not null)
        {
            if (definition.Source is ProjectContainerSource { RuntimeImage: { } declaredOnSource }
                && !string.Equals(declaredOnApi, declaredOnSource, StringComparison.Ordinal))
            {
                throw new FrameworkConfigurationException(
                    $"'{definition.GetType().Name}' declares the image '{declaredOnApi}' and its source declares '{declaredOnSource}', so the one that would win is not obvious.",
                    ["Remove one of the two declarations."]);
            }

            return declaredOnApi;
        }

        return plan.RuntimeImage
            ?? spec.ResolveImage(plan.TargetFramework ?? throw new FrameworkConfigurationException(
                $"The source of API '{definition.Identifier}' names no image and no target framework to derive one from.",
                ["Call WithImage(\"...\") on the definition to name the runtime image."]));
    }

    private static IContainer BuildContainer(PlannedApi planned, INetwork network, string networkAlias)
    {
        DockerApiSpec spec = planned.Spec;
        ContainerSourcePlan plan = planned.Plan;
        string settingsFileName = planned.SettingsFileName;
        string settingsJson = planned.SettingsJson;
        string port = spec.InternalPort.ToString(CultureInfo.InvariantCulture);

        ContainerBuilder builder = new ContainerBuilder(planned.Image)
            .WithNetwork(network)
            .WithNetworkAliases(networkAlias)
            .WithPortBinding(spec.InternalPort, true)
            .WithCreateParameterModifier(ContainerPortBinding.Apply)
            .WithWorkingDirectory(DockerWebDefaults.ApiRoot)
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", spec.EnvironmentName)
            .WithEnvironment("DOTNET_ENVIRONMENT", spec.EnvironmentName)
            .WithEnvironment("ASPNETCORE_HTTP_PORTS", port)
            // Container paths are always separated by '/', whatever the host does.
            .WithResourceMapping(Encoding.UTF8.GetBytes(settingsJson), $"{DockerWebDefaults.ApiRoot}/{settingsFileName}");

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

    /// <summary>
    /// Publishes where this application ended up, for both viewpoints.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Published rather than written back into <c>TestFramework.Web</c>'s configuration store. That store
    /// holds what a person declared; a container's port is chosen by the operating system when it starts, so
    /// where this application ended up is a resource value and the run holds those.
    /// </para>
    /// <para>
    /// Both viewpoints, which the store could not express at all: the test process reaches the mapped host
    /// port and a peer container reaches the network alias, and a store with one <c>BaseUrl</c> forced
    /// everything inside the network to be given the address that only works outside it.
    /// </para>
    /// <para>
    /// The health path is published only when the definition states one. Left unpublished, a configured entry
    /// keeps whatever it declared and an entry that does not exist falls back to the record's own default,
    /// which is what the container default was a copy of.
    /// </para>
    /// </remarks>
    private static void Publish(RunContext context, StartedApi api)
    {
        string identifier = api.Planned.Definition.Identifier;
        EnvironmentResources resources = PublishOn(context);

        resources.Produce(WebEnvironmentResourceKinds.RestApiKind, identifier, ValueNames.BaseUrl, ResourceVantage.Host, api.BaseUrl.ToString());
        resources.Produce(WebEnvironmentResourceKinds.RestApiKind, identifier, ValueNames.BaseUrl, ResourceVantage.Network, api.NetworkBaseUrl.ToString());

        if (api.Planned.Spec.HealthPath is { Length: > 0 } healthPath)
        {
            resources.Produce(WebEnvironmentResourceKinds.RestApiKind, identifier, nameof(ApiConfig.HealthPath), healthPath);
        }
    }
}
