using TestFramework.Core.Environment.Graph;
using System.Text;
using TestFramework.Core.Steps;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Extensions.DependencyInjection;
using TestFramework.Container.Sources;
using TestFramework.Container.Web.Sites;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;
using TestFramework.Core.Exceptions;
using TestFramework.Core.Logging;
using TestFramework.Core.Variables;
using TestFramework.Web;
using TestFramework.Web.Configuration;
using TestFramework.Web.Site;
using TestFramework.Web.Stub;

namespace TestFramework.Container.Web.Components;

/// <summary>
/// Serves the declared sites from containers and publishes their addresses.
/// </summary>
internal sealed class SiteEnvComponent : WebEnvComponentBase
{
    public override EnvComponentIdentifier Id => DockerWebEnvironment.SiteComponentId;

    /// <summary>
    /// Sites are never reused across runs.
    /// </summary>
    /// <remarks>
    /// A reused site would go on serving the payload it started with and proxying to containers that
    /// are gone, so an edit-and-rerun cycle would silently test the previous build.
    /// </remarks>
    public override EnvComponentReuseMode ReuseMode => EnvComponentReuseMode.PerRun;

    public override IReadOnlyList<EnvComponentIdentifier> Dependencies =>
    [
        DockerWebEnvironment.NetworkComponentId,
        DockerWebEnvironment.StubComponentId,
        DockerWebEnvironment.ApiComponentId,
    ];

    public override async Task<object?> CreateAsync(IEnvironmentProvider environment, RunContext context)
    {
        ArgumentNullException.ThrowIfNull(context.Services);
        ArgumentNullException.ThrowIfNull(context.Logger);

        DockerWebEnvironment webEnvironment = GetWebEnvironment(environment);
        IReadOnlyList<DockerSiteDefinition> definitions = webEnvironment.GetSiteDefinitions();
        if (definitions.Count == 0)
            return null;

        INetwork network = webEnvironment.GetRequiredRuntimeState<INetwork>(DockerWebEnvironment.NetworkComponentId);
        SiteTargetResolver targets = new(webEnvironment, context.Services);

        // Declaration order in, declaration order out, however the starts interleave.
        IReadOnlyList<DockerSiteDefinition> ordered = [.. definitions.OrderBy(definition => definition.Identifier.ToString(), StringComparer.Ordinal)];

        // Phase one is serial on purpose: builds share one package cache, and every generated file
        // is logged here so the run states what it serves before serving it.
        List<PlannedSite> planned = [];
        foreach (DockerSiteDefinition definition in ordered)
        {
            DockerSiteSpec spec = definition.Build();
            string identifier = definition.Identifier;

            SiteSourcePlan plan = SiteSourceResolver.Plan(definition.Source);
            foreach (string line in plan.ToLogLines(identifier))
                context.Logger.LogInformation(line);

            EnsureImageSourceServesItself(definition, spec, plan);

            plan = await SitePayloadBuilder.BuildAsync(definition.Source, plan, identifier, context.Logger, context.Deadline.Token).ConfigureAwait(false);

            string? nginxConfig = null;
            if (plan.Kind != SiteSourceKind.Image)
            {
                nginxConfig = NginxConfigFile.Compose(
                    spec.InternalPort,
                    spec.SpaFallback,
                    [.. spec.ProxyRoutes.Select(route => ToNginxLocation(route, targets))],
                    spec.ExtraNginxDirectives);

                context.Logger.LogInformation("Site '{0}' server config '{1}':{2}{3}", identifier, NginxConfigFile.FileName, Environment.NewLine, nginxConfig);
            }

            Dictionary<string, string> generatedFiles = ComposeGeneratedFiles(spec, plan, targets, identifier, context.Logger);

            // The generated files are written into a staged copy of the payload rather than mapped
            // beside it: two mappings writing the same path race on ordering, and the wrong winner
            // would serve the checked-in file instead of the generated one.
            if (plan.Kind != SiteSourceKind.Image && generatedFiles.Count > 0)
                plan = StagePayload(plan, generatedFiles, identifier, context.Logger);

            planned.Add(new PlannedSite(definition, spec, plan, nginxConfig, generatedFiles));
        }

        // Serving images are fetched up front, on the route that also works where the daemon's own
        // pull cannot. A missing image then fails as a stated pull problem, not as a container that
        // mysteriously did not start.
        foreach (string image in planned.Select(ServingImage).Distinct(StringComparer.Ordinal))
            await ContainerImagePull.EnsureAvailableAsync(image, context.Logger, context.Deadline.Token).ConfigureAwait(false);

        // Phase two races: creating, starting and waiting out the containers are independent per
        // site, and the readiness waits are what the setup actually spends its time on.
        IReadOnlyList<StartedSite> started = await ContainerStartCoordinator.StartAllAsync(
            planned,
            (site, token) => StartSiteAsync(site, network, context.Logger, token),
            result => result.Container,
            context.Deadline.Token).ConfigureAwait(false);

        List<RunningSite> sites = [];
        foreach (StartedSite site in started)
        {
            Publish(context, site);
            sites.Add(new RunningSite(site.Planned.Definition.Identifier, site.Container, site.BaseUrl, site.Planned.Plan, site.Planned.NginxConfig, site.Planned.GeneratedFiles));
            context.Logger.LogInformation("Site '{0}' is reachable at '{1}'.", site.Planned.Definition.Identifier, site.BaseUrl);
        }

        SiteComponentState state = new(sites);
        webEnvironment.SetRuntimeState(Id, state);
        return state;
    }

    public override async Task DeconstructAsync(object? state, IEnvironmentProvider environment, RunContext context)
    {
        ArgumentNullException.ThrowIfNull(context.Logger);

        if (state is not SiteComponentState siteState)
            return;

        foreach (RunningSite site in siteState.Sites)
        {
            // The log dies with the container, and a serving failure is invisible from the test side
            // without it.
            await ContainerLogCapture.CaptureAsync(site.Container, $"Site '{site.Identifier}'", context.Logger, context.Deadline.Token).ConfigureAwait(false);
            await ContainerDockerCommands.ForceRemoveContainerAsync(site.Container, context.Deadline.Token).ConfigureAwait(false);

            // A scratch directory the run's own build created is the run's litter. A project's dist
            // built on the host is the caller's artifact and stays.
            if (site.Plan.TemporaryRoot is { } temporaryRoot)
                DeleteTemporaryRoot(temporaryRoot, context.Logger);
        }
    }

    /// <summary>
    /// Returns the alias other containers reach a site by.
    /// </summary>
    /// <param name="identifier">The site identifier.</param>
    internal static string NetworkAlias(string identifier) => $"site-{identifier}";

    internal static void EnsureImageSourceServesItself(DockerSiteDefinition definition, DockerSiteSpec spec, SiteSourcePlan plan)
    {
        if (plan.Kind != SiteSourceKind.Image)
            return;

        if (spec.ProxyRoutes.Count == 0 && spec.ExtraNginxDirectives.Count == 0 && spec.SpaFallback && spec.Image is null)
            return;

        throw new FrameworkConfigurationException(
            $"'{definition.GetType().Name}' declares an image source together with server behaviour (proxy routes, directives, fallback or a serving image). An image runs as-is; its server configuration is baked in.",
            [
                "Bake the routing into the image, or ship the payload as a directory and let the environment serve it.",
                "Generated configuration files stay available for an image source.",
            ]);
    }

    private static NginxProxyLocation ToNginxLocation(DockerSiteProxyRoute route, SiteTargetResolver targets)
        => new(
            route.LocationPath,
            targets.NetworkUrl(route.TargetKind, route.TargetIdentifier),
            route.StripPrefix,
            $"{(route.TargetKind == SiteTargetKind.Api ? "API" : "stub")} '{route.TargetIdentifier}'");

    private static Dictionary<string, string> ComposeGeneratedFiles(
        DockerSiteSpec spec,
        SiteSourcePlan plan,
        SiteTargetResolver targets,
        string identifier,
        ScopedLogger logger)
    {
        Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase);
        SiteAddressBook addresses = targets.CreateAddressBook();

        if (spec.WritesConfigJson)
        {
            Dictionary<string, string> values = new(spec.ConfigJsonValues, StringComparer.OrdinalIgnoreCase);

            // The file is fetched by the browser on the host, so every bound address is the
            // host-mapped one. Handing it a network address would work from another container and
            // fail from the browser.
            foreach (DockerSiteConfigBinding binding in spec.ConfigJsonBindings)
                values[binding.JsonPath] = targets.HostUrl(binding.TargetKind, binding.TargetIdentifier).ToString();

            // The engine composes it: same colon-path JSON as an API's settings, so it is the same
            // document type rather than a second implementation of one.
            files[spec.ConfigJsonPath] = new JsonPathDocument(spec.ConfigJsonPath)
                .Compose(values, ReadExisting(plan, spec.ConfigJsonPath));
        }

        foreach (DockerSiteConfigFile configFile in spec.ConfigFiles)
        {
            SiteConfigFileContext context = new(addresses, configFile.RelativePath, ReadExisting(plan, configFile.RelativePath));
            files[configFile.RelativePath] = configFile.Compose(context)
                ?? throw new FrameworkConfigurationException($"The composer for '{configFile.RelativePath}' of site '{identifier}' returned null.");
        }

        foreach ((string path, string content) in files.OrderBy(file => file.Key, StringComparer.Ordinal))
            logger.LogInformation("Site '{0}' file '{1}':{2}{3}", identifier, path, Environment.NewLine, content);

        return files;
    }

    private static SiteSourcePlan StagePayload(SiteSourcePlan plan, IReadOnlyDictionary<string, string> generatedFiles, string identifier, ScopedLogger logger)
    {
        string staging = Path.Combine(Path.GetTempPath(), $"tf-site-{Guid.NewGuid():N}"[..20]);
        Directory.CreateDirectory(staging);
        int copied = InContainerBuild.CopySources(plan.DistDirectory!, staging);
        logger.LogInformation("Site '{0}' staged {1} payload file(s).", identifier, copied);

        foreach ((string relativePath, string content) in generatedFiles)
        {
            string fullPath = Path.GetFullPath(Path.Combine(staging, relativePath.TrimStart('/')));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        // A build's own scratch directory has served its purpose once the payload is staged.
        if (plan.TemporaryRoot is { } previousRoot)
            DeleteTemporaryRoot(previousRoot, logger);

        return plan with { DistDirectory = staging, TemporaryRoot = staging };
    }

    private static string? ReadExisting(SiteSourcePlan plan, string relativePath)
    {
        // An image source has no payload on disk, so there is no current content to offer.
        if (plan.DistDirectory is not { } distDirectory)
            return null;

        string fullPath = Path.GetFullPath(Path.Combine(distDirectory, relativePath));
        return File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
    }

    private static async Task<StartedSite> StartSiteAsync(PlannedSite planned, INetwork network, ScopedLogger logger, CancellationToken cancellationToken)
    {
        string identifier = planned.Definition.Identifier;
        IContainer container = BuildContainer(planned, network, NetworkAlias(identifier));

        await StartAsync(container, identifier, ServingImage(planned), logger, cancellationToken).ConfigureAwait(false);

        Uri baseUrl = ContainerEndpoints.HostEndpoint(container, planned.Spec.InternalPort);
        await WaitForReadinessAsync(container, identifier, planned.Spec, baseUrl, logger, cancellationToken).ConfigureAwait(false);

        return new StartedSite(planned, container, baseUrl);
    }

    private static string ServingImage(PlannedSite planned)
        => planned.Plan.Kind == SiteSourceKind.Image
            ? planned.Plan.Image!
            : planned.Spec.Image ?? DockerWebDefaults.SiteImage;

    private static IContainer BuildContainer(PlannedSite planned, INetwork network, string alias)
    {
        ContainerBuilder builder = new ContainerBuilder(ServingImage(planned))
            .WithNetwork(network)
            .WithNetworkAliases(alias)
            .WithPortBinding(planned.Spec.InternalPort, true)
            .WithCreateParameterModifier(ContainerPortBinding.Apply);

        if (planned.Plan.Kind != SiteSourceKind.Image)
        {
            // The payload is copied rather than bind-mounted, so nothing is written back into the
            // project's own output. Generated files are already inside the staged payload.
            builder = builder
                .WithResourceMapping(new DirectoryInfo(planned.Plan.DistDirectory!), DockerWebDefaults.SiteContentRoot)
                .WithResourceMapping(NginxConfigFile.ToBytes(planned.NginxConfig!), DockerWebDefaults.SiteNginxConfPath);

            return builder.Build();
        }

        foreach ((string path, string content) in planned.GeneratedFiles)
        {
            // A path starting with '/' addresses the container directly, which is how a generated
            // file reaches an image whose content root this package does not know.
            string containerPath = path.StartsWith('/') ? path : $"{DockerWebDefaults.SiteContentRoot}/{path}";
            builder = builder.WithResourceMapping(Encoding.UTF8.GetBytes(content), containerPath);
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
            await ContainerLogCapture.CaptureAsync(container, $"Site '{identifier}'", logger, cancellationToken).ConfigureAwait(false);
            throw new FrameworkStateException(
                $"The container for site '{identifier}' did not start on image '{image}'.",
                ["Read the captured container log in the run output; the server names the configuration it refused."],
                null,
                exception);
        }
    }

    private static async Task WaitForReadinessAsync(IContainer container, string identifier, DockerSiteSpec spec, Uri baseUrl, ScopedLogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await ContainerReadiness.WaitForHttpAsync(baseUrl, spec.ReadinessPath, spec.ReadinessTimeout, $"Site '{identifier}'", logger, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Capturing before rethrowing is the difference between a bare timeout and the server's
            // own account of what it refused.
            await ContainerLogCapture.CaptureAsync(container, $"Site '{identifier}'", logger, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static void DeleteTemporaryRoot(string directory, ScopedLogger logger)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A temporary directory left behind is untidy, never a reason to fail a run.
            logger.LogWarning($"The site build directory '{directory}' could not be removed: {exception.Message}");
        }
    }

    /// <summary>
    /// Publishes where this site ended up, for both viewpoints.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Published rather than written back into <c>TestFramework.Web</c>'s configuration store. That store
    /// holds what a person declared; a container's port is chosen by the operating system when it starts, so
    /// where this site ended up is a resource value and the run holds those.
    /// </para>
    /// <para>
    /// The network address is built from the alias this container was given and the port it serves on, both
    /// of which are stated a few lines above rather than guessed. The store had one <c>BaseUrl</c> and so
    /// could only ever hold the host one, which is why a browser bridge and a site's own route resolver each
    /// grew their own way of asking the same question.
    /// </para>
    /// </remarks>
    private static void Publish(RunContext context, StartedSite site)
    {
        string identifier = site.Planned.Definition.Identifier;
        EnvironmentResources resources = PublishOn(context);

        resources.Produce(WebEnvironmentResourceKinds.SiteKind, identifier, ValueNames.BaseUrl, ResourceVantage.Host, site.BaseUrl.ToString());
        resources.Produce(
            WebEnvironmentResourceKinds.SiteKind,
            identifier,
            ValueNames.BaseUrl,
            ResourceVantage.Network,
            $"http://{NetworkAlias(identifier)}:{site.Planned.Spec.InternalPort}/");
    }

    private sealed record PlannedSite(
        DockerSiteDefinition Definition,
        DockerSiteSpec Spec,
        SiteSourcePlan Plan,
        string? NginxConfig,
        IReadOnlyDictionary<string, string> GeneratedFiles);

    private sealed record StartedSite(PlannedSite Planned, IContainer Container, Uri BaseUrl);

    /// <summary>
    /// Resolves a route or binding target to an address: a container the environment declared wins;
    /// otherwise the address the target's configuration entry names, so the same site definition
    /// serves a containerized and a deployed backend.
    /// </summary>
    private sealed class SiteTargetResolver(DockerWebEnvironment webEnvironment, IServiceProvider serviceProvider)
    {
        public Uri NetworkUrl(SiteTargetKind kind, string identifier)
            => Resolve(kind, identifier, networkFacing: true);

        public Uri HostUrl(SiteTargetKind kind, string identifier)
            => Resolve(kind, identifier, networkFacing: false);

        public SiteAddressBook CreateAddressBook()
            => new(identifier => HostUrl(SiteTargetKind.Api, identifier), identifier => HostUrl(SiteTargetKind.Stub, identifier));

        private Uri Resolve(SiteTargetKind kind, string identifier, bool networkFacing)
        {
            if (kind == SiteTargetKind.Api)
            {
                if (webEnvironment.GetApiDefinitions().Any(definition => string.Equals(definition.Identifier.Identifier, identifier, StringComparison.Ordinal)))
                {
                    RunningApi api = webEnvironment.GetRequiredRuntimeState<ApiComponentState>(DockerWebEnvironment.ApiComponentId).GetRequiredApi(identifier);
                    return networkFacing ? api.NetworkBaseUrl : api.BaseUrl;
                }

                if (TryGetConfiguredBaseUrl<TestFramework.Web.Configuration.ApiConfig>(identifier, config => config.BaseUrl) is { } configured)
                    return configured;

                throw TargetUnresolvable("API", identifier, $"Include the {nameof(DockerApiDefinition)} for it, or configure 'Api:{identifier}:BaseUrl'.");
            }

            if (webEnvironment.GetStubDefinitions().Any(definition => string.Equals(definition.Identifier.Identifier, identifier, StringComparison.Ordinal)))
            {
                RunningStub stub = webEnvironment.GetRequiredRuntimeState<StubComponentState>(DockerWebEnvironment.StubComponentId).GetRequiredStub(identifier);
                return networkFacing ? stub.NetworkBaseUrl : stub.HostBaseUrl;
            }

            if (TryGetConfiguredBaseUrl<StubConfig>(identifier, config => config.BaseUrl) is { } configuredStub)
                return configuredStub;

            throw TargetUnresolvable("stub", identifier, $"Include the {nameof(StubDefinition)} for it, or configure 'Stub:{identifier}:BaseUrl'.");
        }

        private Uri? TryGetConfiguredBaseUrl<TConfig>(string identifier, Func<TConfig, string> selectBaseUrl)
        {
            WebConfigStore<TConfig>? store = serviceProvider.GetService<WebConfigStore<TConfig>>();
            if (store is null || !store.TryGetConfig(identifier, out TConfig? config) || config is null)
                return null;

            string baseUrl = selectBaseUrl(config);
            return string.IsNullOrWhiteSpace(baseUrl) ? null : new Uri(baseUrl, UriKind.Absolute);
        }

        private static FrameworkConfigurationException TargetUnresolvable(string kind, string identifier, string recovery)
            => new(
                $"A site binds to the {kind} identifier '{identifier}', which no included definition declares and no configuration entry names.",
                [recovery]);
    }
}
