using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
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
using TestFramework.Web.Stub;
using TestFramework.Web.Stub.Mappings;

namespace TestFramework.Container.Web.Components;

/// <summary>
/// Runs a stub server per declared stub and publishes its address.
/// </summary>
internal sealed class StubEnvComponent : WebEnvComponentBase
{
    public override EnvComponentIdentifier Id => DockerWebEnvironment.StubComponentId;

    /// <summary>
    /// Stub servers are never reused across runs.
    /// </summary>
    /// <remarks>
    /// A stub carries a request log, and that log is what assertions read. Reusing the server would
    /// carry a previous run's calls into this one.
    /// </remarks>
    public override EnvComponentReuseMode ReuseMode => EnvComponentReuseMode.PerRun;

    public override IReadOnlyList<EnvComponentIdentifier> Dependencies => [DockerWebEnvironment.NetworkComponentId];

    public override async Task<object?> CreateAsync(IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(logger);

        DockerWebEnvironment webEnvironment = GetWebEnvironment(environment);
        IReadOnlyList<StubDefinition> definitions = webEnvironment.GetStubDefinitions();
        if (definitions.Count == 0)
            return null;

        WebConfigStore<StubConfig> configStore = GetRequiredConfigStore(serviceProvider);
        INetwork network = webEnvironment.GetRequiredRuntimeState<INetwork>(DockerWebEnvironment.NetworkComponentId);
        List<RunningStub> stubs = [];

        foreach (StubDefinition definition in definitions)
        {
            IReadOnlyList<StubMapping> mappings = definition.Build();
            string alias = NetworkAlias(definition.Identifier);
            IContainer container = BuildContainer(webEnvironment, definition.Identifier, mappings, network, alias);

            logger.LogInformation("Stub '{0}' starts with {1} mapping(s) on image '{2}'.", definition.Identifier.ToString(), mappings.Count, webEnvironment.StubImage);

            await StartAsync(container, definition.Identifier, webEnvironment.StubImage, logger, cancellationToken).ConfigureAwait(false);

            Uri hostBaseUrl = ContainerEndpoints.HostEndpoint(container, DockerWebDefaults.StubInternalPort);
            Uri networkBaseUrl = ContainerEndpoints.NetworkEndpoint(alias, DockerWebDefaults.StubInternalPort);

            await WaitForReadinessAsync(container, definition.Identifier, hostBaseUrl, logger, cancellationToken).ConfigureAwait(false);

            Publish(configStore, definition.Identifier, hostBaseUrl);
            int loaded = await CountLoadedMappingsAsync(serviceProvider, definition.Identifier, cancellationToken).ConfigureAwait(false);
            await EnsureMappingsLoadedAsync(container, definition.Identifier, mappings.Count, loaded, logger, cancellationToken).ConfigureAwait(false);

            stubs.Add(new RunningStub(definition.Identifier, container, hostBaseUrl, networkBaseUrl, loaded));
            logger.LogInformation("Stub '{0}' is reachable at '{1}' with {2} mapping(s) loaded.", definition.Identifier.ToString(), hostBaseUrl, loaded);
        }

        StubComponentState state = new(stubs);
        webEnvironment.SetRuntimeState(Id, state);
        return state;
    }

    public override async Task DeconstructAsync(object? state, IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (state is not StubComponentState stubState)
            return;

        foreach (RunningStub stub in stubState.Stubs)
        {
            await ContainerLogCapture.CaptureAsync(stub.Container, $"Stub '{stub.Identifier}'", logger, cancellationToken).ConfigureAwait(false);
            await ContainerDockerCommands.ForceRemoveContainerAsync(stub.Container, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns the alias other containers reach a stub by.
    /// </summary>
    /// <param name="identifier">The stub identifier.</param>
    internal static string NetworkAlias(string identifier) => $"stub-{identifier}";

    private static IContainer BuildContainer(
        DockerWebEnvironment webEnvironment,
        string identifier,
        IReadOnlyList<StubMapping> mappings,
        INetwork network,
        string alias)
    {
        ContainerBuilder builder = new ContainerBuilder(webEnvironment.StubImage)
            .WithNetwork(network)
            .WithNetworkAliases(alias)
            .WithPortBinding(DockerWebDefaults.StubInternalPort, true)
            // The server ignores its mapping folder unless it is told to read it, and says nothing
            // about the files it never looked at.
            .WithCommand("--ReadStaticMappings", "true", "--WireMockLogger", "WireMockConsoleLogger");

        // One file per mapping, numbered in declaration order, so the folder reads the way the
        // definition does and a rejected mapping is identifiable by name.
        for (int index = 0; index < mappings.Count; index++)
        {
            string fileName = StubMappingJson.FileName(identifier, index + 1);
            builder = builder.WithResourceMapping(
                Encoding.UTF8.GetBytes(StubMappingJson.Write(mappings[index])),
                $"{DockerWebDefaults.StubMappingsRoot}/{fileName}");
        }

        return builder.Build();
    }

    private static async Task StartAsync(IContainer container, string identifier, string image, ScopedLogger logger, CancellationToken cancellationToken)
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
            await ContainerLogCapture.CaptureAsync(container, $"Stub '{identifier}'", logger, cancellationToken).ConfigureAwait(false);
            throw new FrameworkStateException(
                $"The container for stub '{identifier}' did not start on image '{image}'.",
                ["Read the captured container log in the run output."],
                null,
                exception);
        }
    }

    private static async Task WaitForReadinessAsync(IContainer container, string identifier, Uri hostBaseUrl, ScopedLogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await ContainerReadiness.WaitForHttpAsync(
                hostBaseUrl,
                $"{DockerWebDefaults.StubAdminPath}/mappings",
                DockerWebDefaults.StubReadinessTimeout,
                $"Stub '{identifier}'",
                logger,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            await ContainerLogCapture.CaptureAsync(container, $"Stub '{identifier}'", logger, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static Task<int> CountLoadedMappingsAsync(IServiceProvider serviceProvider, string identifier, CancellationToken cancellationToken)
        => StubConfigResolver.CreateAdminClient(serviceProvider, identifier).GetMappingCountAsync(cancellationToken);

    private static async Task EnsureMappingsLoadedAsync(IContainer container, string identifier, int declared, int loaded, ScopedLogger logger, CancellationToken cancellationToken)
    {
        if (loaded >= declared)
            return;

        // A mapping the server rejected is simply absent, and every later call would answer 404 for
        // no visible reason. Saying so here is the difference between a clear failure and a hunt.
        await ContainerLogCapture.CaptureAsync(container, $"Stub '{identifier}'", logger, cancellationToken).ConfigureAwait(false);

        throw new FrameworkStateException(
            $"Stub '{identifier}' declared {declared.ToString(CultureInfo.InvariantCulture)} mapping(s) but the server loaded {loaded.ToString(CultureInfo.InvariantCulture)}.",
            [
                "Read the captured container log; the server names the mapping file it rejected.",
                "A mapping the server does not accept never answers, so every call to it would return 404.",
            ]);
    }

    private static WebConfigStore<StubConfig> GetRequiredConfigStore(IServiceProvider serviceProvider)
        => serviceProvider.GetService<WebConfigStore<StubConfig>>()
        ?? throw new FrameworkConfigurationException(
            "The run has no stub configuration store, so the container has nowhere to publish its address. "
            + "Call LoadWebConfig() on the config instance the run is set up with.");

    private static void Publish(WebConfigStore<StubConfig> configStore, string identifier, Uri baseUrl)
    {
        StubConfig published = configStore.TryGetConfig(identifier, out StubConfig? existing) && existing is not null
            ? existing with { BaseUrl = baseUrl.ToString() }
            : new StubConfig { BaseUrl = baseUrl.ToString() };

        configStore.AddConfig(identifier, published with { AdminPath = DockerWebDefaults.StubAdminPath });
    }
}
