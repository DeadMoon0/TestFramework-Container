using TestFramework.Core.Environment.Graph;
using System;
using System.Collections.Generic;
using System.Linq;
using TestFramework.Container.Web.Sites;
using TestFramework.Core.Exceptions;
using TestFramework.Web.Site;
using TestFramework.Web.Stub;

namespace TestFramework.Container.Web;

/// <summary>
/// Declares a static site served by a container the environment starts.
/// </summary>
/// <remarks>
/// A site is the application a browser loads: an Angular build, any other npm framework's output,
/// or a plain folder of pages. A fixed serving image gets the payload copied in; the
/// <see cref="Source"/> only says where the payload comes from, so nothing framework-specific is
/// baked into the container kind.
/// </remarks>
/// <example>
/// <code>
/// internal sealed class ShopSiteDefinition : DockerSiteDefinition
/// {
///     public override SiteIdentifier Identifier =&gt; "shop";
///
///     public override SiteSource Source =&gt; SiteSource.NpmProject("../../Shop.Frontend");
///
///     protected override void Configure(DockerSiteBuilder builder) =&gt; builder
///         .ProxyApi&lt;OrdersApiDefinition&gt;("/api");
/// }
/// </code>
/// </example>
public abstract class DockerSiteDefinition : DockerWebDefinition
{
    /// <summary>
    /// The site identifier browser steps use to reach this site.
    /// </summary>
    public abstract SiteIdentifier Identifier { get; }

    /// <summary>
    /// Where the payload comes from: an image, a built directory, or a project the environment builds.
    /// </summary>
    public abstract SiteSource Source { get; }

    /// <summary>
    /// Declares the serving, routing and runtime configuration of the site.
    /// </summary>
    /// <param name="builder">The builder collecting the declaration.</param>
    protected abstract void Configure(DockerSiteBuilder builder);

    /// <summary>
    /// Builds the declaration this definition describes.
    /// </summary>
    /// <exception cref="FrameworkConfigurationException">The declaration is inconsistent.</exception>
    public DockerSiteSpec Build()
    {
        DockerSiteBuilder builder = new(GetType());
        Configure(builder);
        return builder.Build();
    }
}

/// <summary>
/// What a proxy route or configuration binding points at.
/// </summary>
public enum SiteTargetKind
{
    /// <summary>A declared or configured REST API.</summary>
    Api,

    /// <summary>A declared stub.</summary>
    Stub,
}

/// <summary>
/// One path prefix the site's server forwards to another resource.
/// </summary>
/// <param name="LocationPath">The path prefix on the site's own origin, for example <c>/api</c>.</param>
/// <param name="TargetKind">What the route points at.</param>
/// <param name="TargetIdentifier">The identifier of the target resource.</param>
/// <param name="StripPrefix">Whether the matched prefix is removed before forwarding.</param>
/// <param name="DeclaredByType">Whether the target was named as a definition type, which is validated against the environment's declarations.</param>
public sealed record DockerSiteProxyRoute(string LocationPath, SiteTargetKind TargetKind, string TargetIdentifier, bool StripPrefix, bool DeclaredByType);

/// <summary>
/// One value in the generated runtime configuration file that carries a resource's address.
/// </summary>
/// <param name="JsonPath">The colon-separated path in the generated file.</param>
/// <param name="TargetKind">What the value points at.</param>
/// <param name="TargetIdentifier">The identifier of the target resource.</param>
/// <param name="DeclaredByType">Whether the target was named as a definition type, which is validated against the environment's declarations.</param>
public sealed record DockerSiteConfigBinding(string JsonPath, SiteTargetKind TargetKind, string TargetIdentifier, bool DeclaredByType);

/// <summary>
/// One custom configuration file a definition composes for the payload.
/// </summary>
/// <param name="RelativePath">The payload-relative path the file is written to.</param>
/// <param name="Compose">Turns the resolved addresses and the file's current content into the file's content.</param>
public sealed record DockerSiteConfigFile(string RelativePath, Func<SiteConfigFileContext, string> Compose);

/// <summary>
/// The resolved, browser-facing addresses handed to a configuration composer.
/// </summary>
/// <remarks>
/// Every address is host-mapped: the file is fetched and used by a browser on the test host, never
/// by another container.
/// </remarks>
public sealed class SiteAddressBook
{
    private readonly Func<string, Uri> resolveApi;
    private readonly Func<string, Uri> resolveStub;

    internal SiteAddressBook(Func<string, Uri> resolveApi, Func<string, Uri> resolveStub)
    {
        this.resolveApi = resolveApi;
        this.resolveStub = resolveStub;
    }

    /// <summary>
    /// The address of a REST API: the container the environment started for it, or the address its
    /// configuration entry names when no container serves it.
    /// </summary>
    /// <param name="identifier">The API identifier.</param>
    /// <exception cref="FrameworkConfigurationException">Neither a container nor a configuration entry answers for the identifier.</exception>
    public Uri ApiBaseUrl(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return this.resolveApi(identifier);
    }

    /// <summary>
    /// The address of a stub, from the container the environment started for it.
    /// </summary>
    /// <param name="identifier">The stub identifier.</param>
    /// <exception cref="FrameworkConfigurationException">No stub answers for the identifier.</exception>
    public Uri StubBaseUrl(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return this.resolveStub(identifier);
    }
}

/// <summary>
/// What a configuration composer works with: the resolved addresses and the file as the payload
/// ships it.
/// </summary>
public sealed class SiteConfigFileContext
{
    internal SiteConfigFileContext(SiteAddressBook addresses, string relativePath, string? existingContent)
    {
        Addresses = addresses;
        RelativePath = relativePath;
        ExistingContent = existingContent;
    }

    /// <summary>
    /// Which file is being composed, relative to the payload. Named in anything that goes wrong with it.
    /// </summary>
    public string RelativePath { get; }

    /// <summary>
    /// The resolved, browser-facing addresses.
    /// </summary>
    public SiteAddressBook Addresses { get; }

    /// <summary>
    /// The file as it sits in the built payload, or <see langword="null"/> when the payload has none.
    /// Always <see langword="null"/> for an image source, whose payload is not on disk.
    /// </summary>
    public string? ExistingContent { get; }

    /// <summary>
    /// Merges values over <see cref="ExistingContent"/>: everything not named stays as the payload
    /// shipped it, so one field can be overridden without rebuilding the whole file.
    /// </summary>
    /// <param name="overrides">The values to set, each a colon-separated path and its value.</param>
    /// <exception cref="FrameworkConfigurationException">The existing content is not a JSON object.</exception>
    public string MergeJson(params (string JsonPath, string Value)[] overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        return new JsonPathDocument(RelativePath).Compose(
            overrides.ToDictionary(pair => pair.JsonPath, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            ExistingContent);
    }
}

/// <summary>
/// Collects how one site is served.
/// </summary>
/// <param name="definitionType">The definition being configured, named in error messages.</param>
public sealed class DockerSiteBuilder(Type definitionType)
{
    private readonly List<DockerSiteProxyRoute> _proxyRoutes = [];
    private readonly List<string> _extraDirectives = [];
    private readonly List<DockerSiteConfigBinding> _configJsonBindings = [];
    private readonly Dictionary<string, string> _configJsonValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DockerSiteConfigFile> _configFiles = [];
    private string? _image;
    private int _internalPort = DockerWebDefaults.SiteInternalPort;
    private string _readinessPath = "/";
    private TimeSpan _readinessTimeout = DockerWebDefaults.SiteReadinessTimeout;
    private bool _spaFallback = true;
    private string _configJsonPath = DockerWebDefaults.SiteConfigJsonPath;

    /// <summary>
    /// Overrides the serving image. It must be nginx-compatible, because the generated server
    /// configuration is nginx syntax; a different web server belongs in a <see cref="SiteSource.Image"/> source.
    /// </summary>
    /// <param name="image">The image to serve with.</param>
    public DockerSiteBuilder WithImage(string image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        _image = image;
        return this;
    }

    /// <summary>
    /// Sets the port the server listens on inside the container.
    /// </summary>
    /// <param name="internalPort">The container port.</param>
    public DockerSiteBuilder WithPort(int internalPort)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(internalPort);
        _internalPort = internalPort;
        return this;
    }

    /// <summary>
    /// Sets the path probed until the site answers. Defaults to <c>/</c>.
    /// </summary>
    /// <param name="path">The path, relative to the base address.</param>
    public DockerSiteBuilder WithReadinessPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _readinessPath = path.StartsWith('/') ? path : $"/{path}";
        return this;
    }

    /// <summary>
    /// Sets how long to wait for the site to answer before failing the setup.
    /// </summary>
    /// <param name="readinessTimeout">The readiness timeout.</param>
    public DockerSiteBuilder WithReadinessTimeout(TimeSpan readinessTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(readinessTimeout, TimeSpan.Zero);
        _readinessTimeout = readinessTimeout;
        return this;
    }

    /// <summary>
    /// Serves unknown paths as <c>404</c> instead of falling back to <c>index.html</c>, for a site
    /// that is not a single-page application.
    /// </summary>
    public DockerSiteBuilder WithoutSpaFallback()
    {
        _spaFallback = false;
        return this;
    }

    /// <summary>
    /// Forwards a path prefix to a declared application, over the environment's network.
    /// </summary>
    /// <typeparam name="TApi">The application definition to forward to.</typeparam>
    /// <param name="locationPath">The path prefix on the site's own origin, for example <c>/api</c>.</param>
    /// <param name="stripPrefix">Whether the matched prefix is removed before forwarding.</param>
    /// <remarks>
    /// This is the same-origin road: the browser calls the site's own origin, so no cross-origin
    /// permission is needed in the application, exactly like a production reverse proxy.
    /// </remarks>
    public DockerSiteBuilder ProxyApi<TApi>(string locationPath, bool stripPrefix = false)
        where TApi : DockerApiDefinition, new()
        => AddProxyRoute(locationPath, SiteTargetKind.Api, new TApi().Identifier, stripPrefix, declaredByType: true);

    /// <summary>
    /// Forwards a path prefix to an application named by identifier, which may live outside this
    /// environment.
    /// </summary>
    /// <param name="apiIdentifier">The API identifier.</param>
    /// <param name="locationPath">The path prefix on the site's own origin.</param>
    /// <param name="stripPrefix">Whether the matched prefix is removed before forwarding.</param>
    /// <remarks>
    /// Resolved when the environment starts: a container the environment declared wins; otherwise
    /// the address the API's configuration entry names is used, so the same definition serves a
    /// containerized and a deployed backend.
    /// </remarks>
    public DockerSiteBuilder ProxyApi(string apiIdentifier, string locationPath, bool stripPrefix = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiIdentifier);
        return AddProxyRoute(locationPath, SiteTargetKind.Api, apiIdentifier, stripPrefix, declaredByType: false);
    }

    /// <summary>
    /// Forwards a path prefix to a declared stub, over the environment's network.
    /// </summary>
    /// <typeparam name="TStub">The stub definition to forward to.</typeparam>
    /// <param name="locationPath">The path prefix on the site's own origin.</param>
    /// <param name="stripPrefix">Whether the matched prefix is removed before forwarding.</param>
    public DockerSiteBuilder ProxyStub<TStub>(string locationPath, bool stripPrefix = false)
        where TStub : StubDefinition, new()
        => AddProxyRoute(locationPath, SiteTargetKind.Stub, new TStub().Identifier, stripPrefix, declaredByType: true);

    /// <summary>
    /// Appends a raw directive inside the generated server block, for the server behaviour no
    /// declaration covers.
    /// </summary>
    /// <param name="rawDirective">The directive, taken verbatim.</param>
    public DockerSiteBuilder WithNginxServerDirective(string rawDirective)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawDirective);
        _extraDirectives.Add(rawDirective);
        return this;
    }

    /// <summary>
    /// Overrides where the generated runtime configuration file is written, relative to the payload.
    /// </summary>
    /// <param name="relativePath">The payload-relative path, for example <c>assets/config.json</c>.</param>
    public DockerSiteBuilder WithConfigJsonPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        _configJsonPath = relativePath.TrimStart('/');
        return this;
    }

    /// <summary>
    /// Writes a declared application's browser-facing address into the runtime configuration file.
    /// </summary>
    /// <typeparam name="TApi">The application definition to bind to.</typeparam>
    /// <param name="jsonPath">The colon-separated path receiving the address.</param>
    /// <remarks>
    /// This is the direct road for an application that reads an absolute address from its runtime
    /// configuration: the browser then calls the application's origin itself, which needs the
    /// application to allow the site's origin.
    /// </remarks>
    public DockerSiteBuilder ConfigJsonApi<TApi>(string jsonPath)
        where TApi : DockerApiDefinition, new()
        => AddConfigBinding(jsonPath, SiteTargetKind.Api, new TApi().Identifier, declaredByType: true);

    /// <summary>
    /// Writes an application's browser-facing address into the runtime configuration file, named by
    /// identifier, which may live outside this environment.
    /// </summary>
    /// <param name="apiIdentifier">The API identifier.</param>
    /// <param name="jsonPath">The colon-separated path receiving the address.</param>
    public DockerSiteBuilder ConfigJsonApi(string apiIdentifier, string jsonPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiIdentifier);
        return AddConfigBinding(jsonPath, SiteTargetKind.Api, apiIdentifier, declaredByType: false);
    }

    /// <summary>
    /// Writes a declared stub's browser-facing address into the runtime configuration file.
    /// </summary>
    /// <typeparam name="TStub">The stub definition to bind to.</typeparam>
    /// <param name="jsonPath">The colon-separated path receiving the address.</param>
    public DockerSiteBuilder ConfigJsonStub<TStub>(string jsonPath)
        where TStub : StubDefinition, new()
        => AddConfigBinding(jsonPath, SiteTargetKind.Stub, new TStub().Identifier, declaredByType: true);

    /// <summary>
    /// Writes a fixed value into the runtime configuration file.
    /// </summary>
    /// <param name="jsonPath">The colon-separated path receiving the value.</param>
    /// <param name="value">The value.</param>
    public DockerSiteBuilder WithConfigJsonValue(string jsonPath, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);
        ArgumentNullException.ThrowIfNull(value);
        _configJsonValues[jsonPath] = value;
        return this;
    }

    /// <summary>
    /// Composes a configuration file of any shape, for the applications whose runtime configuration
    /// the declared bindings do not fit.
    /// </summary>
    /// <param name="relativePath">The payload-relative path the file is written to.</param>
    /// <param name="compose">
    /// Turns the resolved addresses and the file's current payload content into the file's content.
    /// The result is logged verbatim, like every generated file.
    /// </param>
    public DockerSiteBuilder WithConfigFile(string relativePath, Func<SiteConfigFileContext, string> compose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(compose);
        _configFiles.Add(new DockerSiteConfigFile(relativePath.TrimStart('/'), compose));
        return this;
    }

    /// <summary>
    /// Validates and returns the collected declaration.
    /// </summary>
    /// <exception cref="FrameworkConfigurationException">The declaration is inconsistent.</exception>
    public DockerSiteSpec Build()
    {
        string[] duplicateLocations = [.. _proxyRoutes
            .GroupBy(route => NginxConfigFile.NormalizeLocationPath(route.LocationPath), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)];

        if (duplicateLocations.Length > 0)
            throw new FrameworkConfigurationException($"'{definitionType.Name}' declares the proxy location(s) {string.Join(", ", duplicateLocations.Select(path => $"'{path}'"))} more than once. One prefix is forwarded to one target.");

        if (_proxyRoutes.Any(route => NginxConfigFile.NormalizeLocationPath(route.LocationPath) == "/"))
            throw new FrameworkConfigurationException($"'{definitionType.Name}' proxies the root path '/', which is where the payload itself is served. Proxy a sub-path such as '/api'.");

        string[] boundPaths = [.. _configJsonBindings.Select(binding => binding.JsonPath)];

        string[] collisions = [.. boundPaths
            .Where(path => _configJsonValues.ContainsKey(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        if (collisions.Length > 0)
            throw new FrameworkConfigurationException($"'{definitionType.Name}' sets {string.Join(", ", collisions.Select(path => $"'{path}'"))} both directly and from a resource binding, so the value that would win is not obvious. Remove one of the two.");

        string[] duplicateBindings = [.. boundPaths
            .GroupBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)];

        if (duplicateBindings.Length > 0)
            throw new FrameworkConfigurationException($"'{definitionType.Name}' binds {string.Join(", ", duplicateBindings.Select(path => $"'{path}'"))} to more than one resource.");

        string[] duplicateFiles = [.. _configFiles
            .GroupBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)];

        if (duplicateFiles.Length > 0)
            throw new FrameworkConfigurationException($"'{definitionType.Name}' composes the file(s) {string.Join(", ", duplicateFiles.Select(path => $"'{path}'"))} more than once. One path is composed by one delegate.");

        if ((_configJsonBindings.Count > 0 || _configJsonValues.Count > 0)
            && _configFiles.Any(file => string.Equals(file.RelativePath, _configJsonPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new FrameworkConfigurationException($"'{definitionType.Name}' both binds values into '{_configJsonPath}' and composes the same file with WithConfigFile(...), so the content that would win is not obvious. Use one of the two.");
        }

        return new DockerSiteSpec(
            _image,
            _internalPort,
            _readinessPath,
            _readinessTimeout,
            _spaFallback,
            [.. _proxyRoutes],
            [.. _extraDirectives],
            _configJsonPath,
            [.. _configJsonBindings],
            _configJsonValues,
            [.. _configFiles]);
    }

    private DockerSiteBuilder AddProxyRoute(string locationPath, SiteTargetKind targetKind, string targetIdentifier, bool stripPrefix, bool declaredByType)
    {
        // Normalizing here makes an unusable path fail at declaration, where the definition is on
        // the stack, rather than at environment setup.
        NginxConfigFile.NormalizeLocationPath(locationPath);
        _proxyRoutes.Add(new DockerSiteProxyRoute(locationPath, targetKind, targetIdentifier, stripPrefix, declaredByType));
        return this;
    }

    private DockerSiteBuilder AddConfigBinding(string jsonPath, SiteTargetKind targetKind, string targetIdentifier, bool declaredByType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);
        _configJsonBindings.Add(new DockerSiteConfigBinding(jsonPath, targetKind, targetIdentifier, declaredByType));
        return this;
    }
}

/// <summary>
/// How one site is served.
/// </summary>
/// <param name="Image">An explicit serving image, or <see langword="null"/> for the default.</param>
/// <param name="InternalPort">The port the server listens on inside the container.</param>
/// <param name="ReadinessPath">The path probed until the site answers.</param>
/// <param name="ReadinessTimeout">How long to wait for the site to answer.</param>
/// <param name="SpaFallback">Whether unknown paths fall back to <c>index.html</c>.</param>
/// <param name="ProxyRoutes">The path prefixes forwarded to other resources.</param>
/// <param name="ExtraNginxDirectives">Raw directives appended inside the generated server block.</param>
/// <param name="ConfigJsonPath">Where the generated runtime configuration file is written.</param>
/// <param name="ConfigJsonBindings">Values in the generated file that carry resource addresses.</param>
/// <param name="ConfigJsonValues">Fixed values in the generated file.</param>
/// <param name="ConfigFiles">Custom configuration files composed by the definition.</param>
public sealed record DockerSiteSpec(
    string? Image,
    int InternalPort,
    string ReadinessPath,
    TimeSpan ReadinessTimeout,
    bool SpaFallback,
    IReadOnlyList<DockerSiteProxyRoute> ProxyRoutes,
    IReadOnlyList<string> ExtraNginxDirectives,
    string ConfigJsonPath,
    IReadOnlyList<DockerSiteConfigBinding> ConfigJsonBindings,
    IReadOnlyDictionary<string, string> ConfigJsonValues,
    IReadOnlyList<DockerSiteConfigFile> ConfigFiles)
{
    /// <summary>
    /// Whether the generated runtime configuration file is written at all.
    /// </summary>
    public bool WritesConfigJson => ConfigJsonBindings.Count > 0 || ConfigJsonValues.Count > 0;

    /// <summary>
    /// Returns a readable description of the declaration.
    /// </summary>
    public override string ToString()
        => $"port {InternalPort} ({ProxyRoutes.Count} proxy route(s), {ConfigJsonBindings.Count + ConfigJsonValues.Count} config value(s), {ConfigFiles.Count} composed file(s), SPA fallback {(SpaFallback ? "on" : "off")})";
}
