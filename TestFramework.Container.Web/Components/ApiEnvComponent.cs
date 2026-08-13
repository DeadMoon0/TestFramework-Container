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
        List<RunningApi> apis = [];

        foreach (DockerApiDefinition definition in definitions)
        {
            DockerApiSpec spec = definition.Build();
            ContainerOutput output = ResolveOutput(definition, spec);
            IReadOnlyDictionary<string, string> settings = ComposeSettings(webEnvironment, definition, spec);
            string settingsJson = ApiSettingsFile.Compose(settings);
            string settingsFileName = ApiSettingsFile.FileName(spec.EnvironmentName);
            string image = spec.ResolveImage(output.TargetFramework);

            // Stated rather than implied: what is shipped, from where, and with which settings.
            logger.LogInformation(
                "API '{0}' ships '{1}' ({2}) on image '{3}'.",
                definition.Identifier.ToString(),
                output.OutputDirectory,
                output.TargetFramework,
                image);

            if (output.UsedFallbackOutput)
                logger.LogInformation("API '{0}' used the owning project output. {1}", definition.Identifier.ToString(), output.FallbackReason ?? string.Empty);

            logger.LogInformation("API '{0}' settings '{1}':{2}{3}", definition.Identifier.ToString(), settingsFileName, Environment.NewLine, settingsJson);

            IContainer container = BuildContainer(spec, output, network, settingsFileName, settingsJson, image);
            await StartAsync(container, definition, image, logger, cancellationToken).ConfigureAwait(false);

            Uri baseUrl = ContainerEndpoints.HostEndpoint(container, spec.InternalPort);
            await WaitForReadinessAsync(container, definition, spec, baseUrl, logger, cancellationToken).ConfigureAwait(false);

            Publish(configStore, definition.Identifier, baseUrl, spec.HealthPath);
            apis.Add(new RunningApi(definition.Identifier, container, baseUrl, output.OutputDirectory, settingsFileName, settingsJson));

            logger.LogInformation("API '{0}' is reachable at '{1}'.", definition.Identifier.ToString(), baseUrl);
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
        }
    }

    private static ContainerOutput ResolveOutput(DockerApiDefinition definition, DockerApiSpec spec)
        => spec.OutputDirectory is { } declared
            ? ContainerOutputResolver.ResolveFrom(definition.EntryPointType, declared, [])
            // The application's own output, not the test project's copy of its assembly.
            : ContainerOutputResolver.ResolveProjectOutput(definition.EntryPointType);

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
        ContainerOutput output,
        INetwork network,
        string settingsFileName,
        string settingsJson,
        string image)
    {
        string port = spec.InternalPort.ToString(CultureInfo.InvariantCulture);

        // The output is copied rather than bind-mounted, so the generated settings file can sit
        // beside it without anything being written back into the project's own bin directory.
        ContainerBuilder builder = new ContainerBuilder(image)
            .WithNetwork(network)
            .WithPortBinding(spec.InternalPort, true)
            .WithResourceMapping(new DirectoryInfo(output.OutputDirectory), DockerWebDefaults.ApiRoot)
            .WithResourceMapping(ApiSettingsFile.ToBytes(settingsJson), $"{DockerWebDefaults.ApiRoot}/{settingsFileName}")
            .WithWorkingDirectory(DockerWebDefaults.ApiRoot)
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", spec.EnvironmentName)
            .WithEnvironment("DOTNET_ENVIRONMENT", spec.EnvironmentName)
            .WithEnvironment("ASPNETCORE_HTTP_PORTS", port)
            .WithCommand("dotnet", $"{DockerWebDefaults.ApiRoot}/{output.AssemblyFileName}");

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
