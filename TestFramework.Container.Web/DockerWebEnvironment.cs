using System;
using System.Collections.Generic;
using System.Linq;
using TestFramework.Container.Web.Components;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;
using TestFramework.Core.Exceptions;
using TestFramework.Web;
using TestFramework.Web.Sql;
using TestFramework.Web.Sql.Artifacts;
using TestFramework.Web.Stub;

namespace TestFramework.Container.Web;

/// <summary>
/// Serves the resources a web timeline needs from Docker containers.
/// </summary>
/// <remarks>
/// A timeline written against a deployed database runs here unchanged: it still names an identifier,
/// and this provider decides that the identifier is served by a container it starts. The connection
/// string is published into the same configuration store a settings file would have filled, so
/// nothing downstream knows the difference.
/// </remarks>
/// <example>
/// <code>
/// TimelineRun run = await timeline.SetupRun(config)
///     .SetEnv(DockerWebEnvironment.For&lt;SampleSqlDefinition&gt;())
///     .RunAsync();
/// </code>
/// </example>
public class DockerWebEnvironment : EnvironmentProviderBase
{
    /// <summary>
    /// The component that creates the Docker network the containers share.
    /// </summary>
    public static readonly EnvComponentIdentifier NetworkComponentId = "docker-network";

    /// <summary>
    /// The component that runs SQL Server and provisions the declared databases.
    /// </summary>
    public static readonly EnvComponentIdentifier SqlServerComponentId = "sqlserver";

    /// <summary>
    /// The component that runs the declared applications.
    /// </summary>
    public static readonly EnvComponentIdentifier ApiComponentId = "api";

    /// <summary>
    /// The component that runs the declared stub servers.
    /// </summary>
    public static readonly EnvComponentIdentifier StubComponentId = "stub";

    /// <summary>
    /// The component that serves the declared sites.
    /// </summary>
    public static readonly EnvComponentIdentifier SiteComponentId = "site";

    /// <summary>
    /// The requirement kind browser steps declare for the web application they drive.
    /// </summary>
    /// <remarks>
    /// The string is the contract, not a package reference: it must match
    /// <c>BrowserEnvironmentResourceKinds.WebApp</c> in TestFramework.UI.Browser. The site is the
    /// application a browser loads, so a browser step's own requirement starts and validates the
    /// declared site with no bridging call in between.
    /// </remarks>
    internal const string UiWebAppResourceKind = "ui.webapp";

    private readonly Dictionary<Type, DockerWebDefinition> _definitions = [];
    private readonly Dictionary<Type, StubDefinition> _stubDefinitions = [];
    private readonly Dictionary<EnvComponentIdentifier, object?> _runtimeStates = [];
    private readonly object _runtimeStateGate = new();

    /// <summary>
    /// Creates an environment with no resources declared yet.
    /// </summary>
    public DockerWebEnvironment()
    {
        AddComponent(new WebNetworkEnvComponent());
        AddComponent(new SqlServerEnvComponent());
        AddComponent(new StubEnvComponent());
        AddComponent(new ApiEnvComponent());
        AddComponent(new SiteEnvComponent());

        MapResourceKind(WebEnvironmentResourceKinds.Sql, SqlServerComponentId);
        MapResourceKind(WebEnvironmentResourceKinds.RestApi, ApiComponentId);
        MapResourceKind(WebEnvironmentResourceKinds.Stub, StubComponentId);
        MapResourceKind(WebEnvironmentResourceKinds.Site, SiteComponentId);
        MapResourceKind(UiWebAppResourceKind, SiteComponentId);
        MapArtifact(typeof(SqlRowArtifactDescriber<>), SqlServerComponentId);
    }

    private readonly HashSet<string> _usedSqlIdentifiers = [];
    private readonly HashSet<string> _usedApiIdentifiers = [];
    private readonly HashSet<string> _usedStubIdentifiers = [];
    private readonly HashSet<string> _usedSiteIdentifiers = [];

    /// <summary>
    /// The SQL identifiers the last resolution found in use.
    /// </summary>
    /// <remarks>
    /// Read-only, because <see cref="ResolveComponents"/> clears and refills it. Anything a caller added
    /// by hand would disappear on the next resolution, which is not a thing a public collection should
    /// let you attempt.
    /// </remarks>
    public IReadOnlyCollection<string> UsedSqlIdentifiers => _usedSqlIdentifiers;

    /// <inheritdoc cref="UsedSqlIdentifiers" />
    public IReadOnlyCollection<string> UsedApiIdentifiers => _usedApiIdentifiers;

    /// <inheritdoc cref="UsedSqlIdentifiers" />
    public IReadOnlyCollection<string> UsedStubIdentifiers => _usedStubIdentifiers;

    /// <inheritdoc cref="UsedSqlIdentifiers" />
    public IReadOnlyCollection<string> UsedSiteIdentifiers => _usedSiteIdentifiers;

    /// <summary>
    /// The image the stub servers run.
    /// </summary>
    public string StubImage { get; private set; } = DockerWebDefaults.StubImage;

    /// <summary>
    /// The image the SQL Server container runs.
    /// </summary>
    public string SqlImage { get; private set; } = DockerWebDefaults.MsSqlImage;

    /// <summary>
    /// The <c>sa</c> password of the SQL Server container.
    /// </summary>
    /// <remarks>
    /// Generated per environment, so the login of a running test server is not something a package
    /// download tells you. Call <see cref="UseSqlPassword"/> to pin one instead.
    /// </remarks>
    public string SqlPassword { get; private set; } = MsSqlContainerFactory.CreateMsSqlPassword();

    /// <summary>
    /// The memory limit handed to the SQL Server engine.
    /// </summary>
    public int SqlMemoryLimitMb { get; private set; } = DockerWebDefaults.MsSqlMemoryLimitMb;

    /// <summary>
    /// Creates an environment serving one declared resource.
    /// </summary>
    /// <typeparam name="TDefinition">The definition to include.</typeparam>
    public static DockerWebEnvironment For<TDefinition>()
        where TDefinition : DockerWebDefinition, new()
        => new DockerWebEnvironment().Include<TDefinition>();

    /// <summary>
    /// Declares a resource this environment serves.
    /// </summary>
    /// <typeparam name="TDefinition">The definition to include.</typeparam>
    public DockerWebEnvironment Include<TDefinition>()
        where TDefinition : DockerWebDefinition, new()
        => Include(new TDefinition());

    /// <summary>
    /// Declares a resource this environment serves.
    /// </summary>
    /// <param name="definition">The definition to include.</param>
    /// <exception cref="FrameworkConfigurationException">Two definitions claim the same identifier.</exception>
    public DockerWebEnvironment Include(DockerWebDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        switch (definition)
        {
            case DockerSqlDefinition sql:
                EnsureUniqueIdentifier(GetSqlDefinitions(), sql, existing => existing.Identifier, "SQL");
                break;
            case DockerApiDefinition api:
                EnsureUniqueIdentifier(GetApiDefinitions(), api, existing => existing.Identifier, "API");
                break;
            case DockerSiteDefinition site:
                EnsureUniqueIdentifier(GetSiteDefinitions(), site, existing => existing.Identifier, "site");
                break;
        }

        _definitions[definition.GetType()] = definition;
        return this;
    }

    /// <summary>
    /// Overrides the SQL Server image.
    /// </summary>
    /// <param name="image">The image to run.</param>
    public DockerWebEnvironment UseSqlImage(string image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        SqlImage = image;
        return this;
    }

    /// <summary>
    /// Overrides the <c>sa</c> password of the SQL Server container.
    /// </summary>
    /// <param name="password">The password to set.</param>
    public DockerWebEnvironment UseSqlPassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        SqlPassword = password;
        return this;
    }

    /// <summary>
    /// Overrides the memory limit handed to the SQL Server engine.
    /// </summary>
    /// <param name="memoryLimitMb">The limit in megabytes.</param>
    public DockerWebEnvironment UseSqlMemoryLimit(int memoryLimitMb)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memoryLimitMb);
        SqlMemoryLimitMb = memoryLimitMb;
        return this;
    }

    /// <summary>
    /// Overrides the stub server image.
    /// </summary>
    /// <param name="image">The image to run.</param>
    public DockerWebEnvironment UseStubImage(string image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        StubImage = image;
        return this;
    }

    /// <summary>
    /// Declares a stubbed dependency this environment serves.
    /// </summary>
    /// <typeparam name="TStub">The stub definition to include.</typeparam>
    /// <remarks>
    /// A stub definition says nothing about hosting, so it comes from the web package rather than
    /// this one. Including it here is what decides that a container serves it.
    /// </remarks>
    public DockerWebEnvironment IncludeStub<TStub>()
        where TStub : StubDefinition, new()
        => Include(new TStub());

    /// <summary>
    /// Declares a stubbed dependency this environment serves.
    /// </summary>
    /// <param name="definition">The stub definition to include.</param>
    /// <exception cref="FrameworkConfigurationException">Two definitions claim the same identifier.</exception>
    public DockerWebEnvironment Include(StubDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        EnsureUniqueStubIdentifier(definition);
        _stubDefinitions[definition.GetType()] = definition;
        return this;
    }

    /// <summary>
    /// The SQL databases this environment provisions.
    /// </summary>
    public IReadOnlyList<DockerSqlDefinition> GetSqlDefinitions()
        => [.. _definitions.Values.OfType<DockerSqlDefinition>().OrderBy(definition => definition.Identifier.Identifier, StringComparer.Ordinal)];

    /// <summary>
    /// The applications this environment runs.
    /// </summary>
    public IReadOnlyList<DockerApiDefinition> GetApiDefinitions()
        => [.. _definitions.Values.OfType<DockerApiDefinition>().OrderBy(definition => definition.Identifier.Identifier, StringComparer.Ordinal)];

    /// <summary>
    /// The stubbed dependencies this environment serves.
    /// </summary>
    public IReadOnlyList<StubDefinition> GetStubDefinitions()
        => [.. _stubDefinitions.Values.OrderBy(definition => definition.Identifier.Identifier, StringComparer.Ordinal)];

    /// <summary>
    /// The sites this environment serves.
    /// </summary>
    public IReadOnlyList<DockerSiteDefinition> GetSiteDefinitions()
        => [.. _definitions.Values.OfType<DockerSiteDefinition>().OrderBy(definition => definition.Identifier.Identifier, StringComparer.Ordinal)];

    /// <inheritdoc />
    public override IReadOnlyCollection<EnvComponentIdentifier> ResolveComponents(IEnumerable<ArtifactInstanceGeneric> artifacts, IEnumerable<EnvironmentRequirement> requirements)
    {
        ArgumentNullException.ThrowIfNull(artifacts);

        _usedSqlIdentifiers.Clear();
        _usedApiIdentifiers.Clear();
        _usedStubIdentifiers.Clear();
        _usedSiteIdentifiers.Clear();

        foreach (ArtifactInstanceGeneric artifact in artifacts)
        {
            // The reference states which database it belongs to, so no reflection over artifact
            // types is needed to find out.
            if (artifact.Reference is ISqlArtifactReference sqlReference)
                _usedSqlIdentifiers.Add(sqlReference.SqlIdentifier);
        }

        HashSet<EnvComponentIdentifier> resolved = [.. base.ResolveComponents(artifacts, requirements)];

        // A declared resource is started whether or not this particular timeline touches it: it was
        // asked for, and one SQL Server container serves every database anyway.
        if (GetSqlDefinitions().Count > 0 || UsedSqlIdentifiers.Count > 0)
            resolved.Add(SqlServerComponentId);

        if (GetApiDefinitions().Count > 0)
            resolved.Add(ApiComponentId);

        if (GetStubDefinitions().Count > 0)
            resolved.Add(StubComponentId);

        if (GetSiteDefinitions().Count > 0)
            resolved.Add(SiteComponentId);

        EnsureDeclaredIdentifiers();
        EnsureDeclaredApiBindings();
        EnsureDeclaredSiteBindings();

        return [.. resolved];
    }

    /// <summary>
    /// Publishes the state a created component produced.
    /// </summary>
    /// <remarks>
    /// Internal, because an outside caller overwriting a component's live state mid-run would make
    /// teardown skip the real containers silently. Reachable must not mean manipulable.
    /// </remarks>
    /// <param name="identifier">The component that produced it.</param>
    /// <param name="state">The state.</param>
    internal void SetRuntimeState(EnvComponentIdentifier identifier, object? state)
    {
        lock (_runtimeStateGate)
            _runtimeStates[identifier] = state;
    }

    /// <summary>
    /// Reads the state a component produced earlier in the same setup.
    /// </summary>
    /// <typeparam name="TState">The expected state type.</typeparam>
    /// <param name="identifier">The component that produced it.</param>
    /// <exception cref="FrameworkStateException">The component has not produced that state.</exception>
    internal TState GetRequiredRuntimeState<TState>(EnvComponentIdentifier identifier)
    {
        lock (_runtimeStateGate)
        {
            if (_runtimeStates.TryGetValue(identifier, out object? state) && state is TState typedState)
                return typedState;
        }

        throw new FrameworkStateException($"The runtime state for environment component '{identifier}' is not available.");
    }

    /// <inheritdoc />
    protected override void OnRequirementResolved(EnvironmentRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        if (string.Equals(requirement.ResourceKind, WebEnvironmentResourceKinds.Sql, StringComparison.Ordinal))
            _usedSqlIdentifiers.Add(requirement.ResourceIdentifier);

        if (string.Equals(requirement.ResourceKind, WebEnvironmentResourceKinds.RestApi, StringComparison.Ordinal))
            _usedApiIdentifiers.Add(requirement.ResourceIdentifier);

        if (string.Equals(requirement.ResourceKind, WebEnvironmentResourceKinds.Stub, StringComparison.Ordinal))
            _usedStubIdentifiers.Add(requirement.ResourceIdentifier);

        if (string.Equals(requirement.ResourceKind, WebEnvironmentResourceKinds.Site, StringComparison.Ordinal)
            || string.Equals(requirement.ResourceKind, UiWebAppResourceKind, StringComparison.Ordinal))
        {
            _usedSiteIdentifiers.Add(requirement.ResourceIdentifier);
        }
    }

    private void EnsureUniqueStubIdentifier(StubDefinition candidate)
    {
        StubDefinition? conflicting = GetStubDefinitions().FirstOrDefault(existing =>
            existing.GetType() != candidate.GetType()
            && string.Equals(existing.Identifier.Identifier, candidate.Identifier.Identifier, StringComparison.Ordinal));

        if (conflicting is not null)
            throw new FrameworkConfigurationException($"'{candidate.GetType().Name}' and '{conflicting.GetType().Name}' both declare the stub identifier '{candidate.Identifier}'. One identifier is served by one definition.");
    }

    private static void EnsureUniqueIdentifier<TDefinition>(
        IEnumerable<TDefinition> existingDefinitions,
        TDefinition candidate,
        Func<TDefinition, string> selectIdentifier,
        string kind)
        where TDefinition : DockerWebDefinition
    {
        string identifier = selectIdentifier(candidate);
        TDefinition? conflicting = existingDefinitions.FirstOrDefault(existing =>
            existing.GetType() != candidate.GetType() && string.Equals(selectIdentifier(existing), identifier, StringComparison.Ordinal));

        if (conflicting is not null)
            throw new FrameworkConfigurationException($"'{candidate.GetType().Name}' and '{conflicting.GetType().Name}' both declare the {kind} identifier '{identifier}'. One identifier is served by one definition.");
    }

    private void EnsureDeclaredIdentifiers()
    {
        EnsureDeclared(
            UsedSqlIdentifiers,
            [.. GetSqlDefinitions().Select(definition => definition.Identifier.Identifier)],
            "SQL",
            nameof(DockerSqlDefinition),
            "which database to create");

        EnsureDeclared(
            UsedApiIdentifiers,
            [.. GetApiDefinitions().Select(definition => definition.Identifier.Identifier)],
            "API",
            nameof(DockerApiDefinition),
            "which application to run");

        EnsureDeclared(
            UsedStubIdentifiers,
            [.. GetStubDefinitions().Select(definition => definition.Identifier.Identifier)],
            "stub",
            nameof(StubDefinition),
            "which stub to serve");

        EnsureDeclared(
            UsedSiteIdentifiers,
            [.. GetSiteDefinitions().Select(definition => definition.Identifier.Identifier)],
            "site",
            nameof(DockerSiteDefinition),
            "which site to serve");
    }

    private void EnsureDeclaredSiteBindings()
    {
        HashSet<string> apis = [.. GetApiDefinitions().Select(definition => definition.Identifier.Identifier)];
        HashSet<string> stubs = [.. GetStubDefinitions().Select(definition => definition.Identifier.Identifier)];

        foreach (DockerSiteDefinition site in GetSiteDefinitions())
        {
            DockerSiteSpec spec = site.Build();

            // Only targets named as definition types are validated here: naming a type declares an
            // intent to run the target in this environment. A target named by identifier may live
            // outside it and is resolved against the configuration store at setup.
            EnsureSiteBound(site, apis, TypeBoundTargets(spec, SiteTargetKind.Api), "API", nameof(DockerApiDefinition));
            EnsureSiteBound(site, stubs, TypeBoundTargets(spec, SiteTargetKind.Stub), "stub", nameof(StubDefinition));
        }
    }

    private static IReadOnlyList<string> TypeBoundTargets(DockerSiteSpec spec, SiteTargetKind kind)
        => [.. spec.ProxyRoutes.Where(route => route.DeclaredByType && route.TargetKind == kind).Select(route => route.TargetIdentifier)
            .Concat(spec.ConfigJsonBindings.Where(binding => binding.DeclaredByType && binding.TargetKind == kind).Select(binding => binding.TargetIdentifier))];

    private static void EnsureSiteBound(
        DockerSiteDefinition site,
        HashSet<string> declared,
        IReadOnlyList<string> bound,
        string kind,
        string definitionTypeName)
    {
        string[] missing = [.. bound
            .Where(identifier => !declared.Contains(identifier))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(identifier => identifier, StringComparer.Ordinal)];

        if (missing.Length == 0)
            return;

        throw new FrameworkConfigurationException(
            $"'{site.GetType().Name}' binds to the {kind} identifier(s) {string.Join(", ", missing.Select(identifier => $"'{identifier}'"))}, which no included definition declares. "
            + $"Declared: {(declared.Count == 0 ? "none" : string.Join(", ", declared.OrderBy(identifier => identifier, StringComparer.Ordinal)))}. "
            + $"Include the {definitionTypeName} the site points at, or name the target by identifier to resolve it from configuration.");
    }

    private void EnsureDeclaredApiBindings()
    {
        HashSet<string> databases = [.. GetSqlDefinitions().Select(definition => definition.Identifier.Identifier)];
        HashSet<string> stubs = [.. GetStubDefinitions().Select(definition => definition.Identifier.Identifier)];

        foreach (DockerApiDefinition api in GetApiDefinitions())
        {
            DockerApiSpec spec = api.Build();

            EnsureBound(api, databases, [.. spec.SqlBindings.Select(binding => binding.SqlIdentifier.Identifier)], "SQL", nameof(DockerSqlDefinition), "a database");
            EnsureBound(api, stubs, [.. spec.StubBindings.Select(binding => binding.StubIdentifier.Identifier)], "stub", nameof(StubDefinition), "a stub");
        }
    }

    private static void EnsureBound(
        DockerApiDefinition api,
        HashSet<string> declared,
        IReadOnlyList<string> bound,
        string kind,
        string definitionTypeName,
        string whatItNeeds)
    {
        string[] missing = [.. bound
            .Where(identifier => !declared.Contains(identifier))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(identifier => identifier, StringComparer.Ordinal)];

        if (missing.Length == 0)
            return;

        throw new FrameworkConfigurationException(
            $"'{api.GetType().Name}' binds to the {kind} identifier(s) {string.Join(", ", missing.Select(identifier => $"'{identifier}'"))}, which no included definition declares. "
            + $"Declared: {(declared.Count == 0 ? "none" : string.Join(", ", declared.OrderBy(identifier => identifier, StringComparer.Ordinal)))}. "
            + $"Include the {definitionTypeName} the application needs, so it can be given {whatItNeeds}.");
    }

    private static void EnsureDeclared(
        IReadOnlyCollection<string> used,
        HashSet<string> declared,
        string kind,
        string definitionTypeName,
        string whatItDecides)
    {
        string[] missing = [.. used.Where(identifier => !declared.Contains(identifier)).OrderBy(identifier => identifier, StringComparer.Ordinal)];
        if (missing.Length == 0)
            return;

        throw new FrameworkConfigurationException(
            $"The run uses the {kind} identifier(s) {string.Join(", ", missing.Select(identifier => $"'{identifier}'"))}, which no included definition declares. "
            + $"Declared: {(declared.Count == 0 ? "none" : string.Join(", ", declared.OrderBy(identifier => identifier, StringComparer.Ordinal)))}. "
            + $"Include a {definitionTypeName} for it, so the environment knows {whatItDecides}.");
    }
}
