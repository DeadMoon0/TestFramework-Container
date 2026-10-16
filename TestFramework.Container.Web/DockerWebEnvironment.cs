using System;
using System.Collections.Generic;
using System.Linq;
using TestFramework.Container.Web.Components;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;
using TestFramework.Core.Environment.Graph;
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
public sealed class DockerWebEnvironment : EnvironmentProviderBase, IResourceNodeSource, IRunScopedEnvironmentFactory, IPersistentEnvironmentStateSink
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
    /// The component that applies each database's reset mode to a SQL Server an earlier run started.
    /// </summary>
    public static readonly EnvComponentIdentifier SqlResetComponentId = "sql-reset";

    private readonly Dictionary<Type, DockerWebDefinition> _definitions = [];
    private readonly Dictionary<Type, StubDefinition> _stubDefinitions = [];
    private readonly HashSet<EnvComponentIdentifier> _handedRunning = [];
    private readonly object _handedRunningGate = new();

    /// <summary>
    /// Creates an environment with no resources declared yet.
    /// </summary>
    public DockerWebEnvironment()
    {
        AddComponent(new WebNetworkEnvComponent());
        AddComponent(new SqlServerEnvComponent());
        AddComponent(new SqlResetEnvComponent());
        AddComponent(new StubEnvComponent());
        AddComponent(new ApiEnvComponent());
        AddComponent(new SiteEnvComponent());

        // Each kind is provided for what this environment declares - one container per definition - and
        // for nothing else. A run may therefore reach one API in a container and another deployed
        // somewhere: a requirement for a resource configuration declared never reaches this environment.
        MapDeclaredResources(WebResourceKinds.Sql, SqlServerComponentId);
        MapDeclaredResources(WebResourceKinds.RestApi, ApiComponentId);
        MapDeclaredResources(WebResourceKinds.Stub, StubComponentId);
        MapDeclaredResources(WebResourceKinds.Site, SiteComponentId);
    }

    /// <summary>
    /// What this environment is called on the run's resource list.
    /// </summary>
    public string SourceName => "Docker web definition";

    /// <summary>
    /// Every included definition, as a resource on the run's list - so a step requiring it is checked
    /// before anything starts, and its requirement reaches this environment and no other.
    /// </summary>
    /// <remarks>
    /// Built on each read rather than kept: definitions are included after construction, and a list taken
    /// early would quietly leave out the ones included later. The resources carry no values of their own -
    /// their addresses only exist once the containers start, and are published then.
    /// </remarks>
    public IReadOnlyList<ResourceNode> Nodes => new DefinitionResources(this).Nodes;

    /// <summary>
    /// The image the stub servers run.
    /// </summary>
    public string StubImage { get; private set; } = DockerWebDefaults.StubImage;

    /// <summary>
    /// The image the SQL Server container runs.
    /// </summary>
    public string SqlImage { get; private set; } = DockerWebDefaults.MsSqlImage;

    /// <summary>
    /// The <c>sa</c> password pinned with <see cref="UseSqlPassword"/>, or <see langword="null"/> when each
    /// SQL Server container generates its own.
    /// </summary>
    /// <remarks>
    /// Generated per container rather than held here, so the login of a running test server is not something
    /// a package download tells you, and two runs sharing this instance do not share one either. The running
    /// server's connection strings carry whichever password it was started with.
    /// </remarks>
    internal string? PinnedSqlPassword { get; private set; }

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
        PinnedSqlPassword = password;
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

        HashSet<EnvComponentIdentifier> resolved = [.. base.ResolveComponents(artifacts, requirements)];

        // A declared resource is started whether or not this particular timeline touches it: it was
        // asked for, and one SQL Server container serves every database anyway.
        if (GetSqlDefinitions().Count > 0)
        {
            resolved.Add(SqlServerComponentId);
            resolved.Add(SqlResetComponentId);
        }

        if (GetApiDefinitions().Count > 0)
            resolved.Add(ApiComponentId);

        if (GetStubDefinitions().Count > 0)
            resolved.Add(StubComponentId);

        if (GetSiteDefinitions().Count > 0)
            resolved.Add(SiteComponentId);

        EnsureDeclaredApiBindings();
        EnsureDeclaredSiteBindings();

        return [.. resolved];
    }

    /// <summary>
    /// Creates the instance one run uses: this environment's definitions and settings, and nothing any run was handed.
    /// </summary>
    /// <remarks>
    /// The instance records which components an earlier run left running, and two runs sharing one instance would
    /// each read the other's. Sealed for the same reason: a copy made here would quietly drop whatever a subclass added.
    /// </remarks>
    /// <returns>A fresh environment for one run.</returns>
    public IEnvironmentProvider CreateForRun() => CloneDefinitions();

    /// <summary>
    /// Records that this run is handed a component an earlier run created.
    /// </summary>
    /// <remarks>
    /// The state itself reaches the run's components through the run. What is kept here is only the fact that
    /// it came from elsewhere, which is what decides whether a database a previous run used has to be reset.
    /// </remarks>
    /// <param name="identifier">The component.</param>
    /// <param name="state">What it produced when it was created.</param>
    public void SetPersistentState(EnvComponentIdentifier identifier, object? state)
    {
        lock (_handedRunningGate)
            _handedRunning.Add(identifier);
    }

    /// <summary>
    /// Whether this run was handed the component already running, rather than starting it itself.
    /// </summary>
    /// <param name="identifier">The component.</param>
    internal bool WasHandedRunning(EnvComponentIdentifier identifier)
    {
        lock (_handedRunningGate)
            return _handedRunning.Contains(identifier);
    }

    /// <summary>
    /// A copy carrying every definition and setting, and nothing a run recorded.
    /// </summary>
    internal DockerWebEnvironment CloneDefinitions()
    {
        DockerWebEnvironment clone = new()
        {
            StubImage = StubImage,
            SqlImage = SqlImage,
            PinnedSqlPassword = PinnedSqlPassword,
            SqlMemoryLimitMb = SqlMemoryLimitMb,
        };

        foreach ((Type type, DockerWebDefinition definition) in _definitions)
            clone._definitions[type] = definition;

        foreach ((Type type, StubDefinition definition) in _stubDefinitions)
            clone._stubDefinitions[type] = definition;

        return clone;
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

    /// <summary>
    /// The included definitions as declared resources, read afresh each time the run's list is composed.
    /// </summary>
    private sealed class DefinitionResources(DockerWebEnvironment environment) : DeclaredNodeSource
    {
        public override string SourceName => environment.SourceName;

        protected override IEnumerable<DeclaredResource> Declarations
        {
            get
            {
                foreach (DockerSqlDefinition definition in environment.GetSqlDefinitions())
                    yield return Declared(WebResourceKinds.SqlKind, definition.Identifier.Identifier);

                foreach (DockerApiDefinition definition in environment.GetApiDefinitions())
                    yield return Declared(WebResourceKinds.RestApiKind, definition.Identifier.Identifier);

                foreach (StubDefinition definition in environment.GetStubDefinitions())
                    yield return Declared(WebResourceKinds.StubKind, definition.Identifier.Identifier);

                foreach (DockerSiteDefinition definition in environment.GetSiteDefinitions())
                    yield return Declared(WebResourceKinds.SiteKind, definition.Identifier.Identifier);
            }
        }

        private DeclaredResource Declared(ResourceKind kind, string identifier)
            => new() { Kind = kind, Identifier = identifier, Values = new Dictionary<ValueKey, string>(), DeclaredBy = $"{this.SourceName} '{identifier}'" };
    }
}
