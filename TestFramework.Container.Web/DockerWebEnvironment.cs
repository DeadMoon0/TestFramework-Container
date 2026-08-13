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

    private readonly Dictionary<Type, DockerWebDefinition> _definitions = [];
    private readonly Dictionary<EnvComponentIdentifier, object?> _runtimeStates = [];
    private readonly object _runtimeStateGate = new();

    /// <summary>
    /// Creates an environment with no resources declared yet.
    /// </summary>
    public DockerWebEnvironment()
    {
        AddComponent(new WebNetworkEnvComponent());
        AddComponent(new SqlServerEnvComponent());

        MapResourceKind(WebEnvironmentResourceKinds.Sql, SqlServerComponentId);
        MapArtifact(typeof(SqlRowArtifactDescriber<>), SqlServerComponentId);
    }

    /// <summary>
    /// The SQL identifiers the last resolution found in use.
    /// </summary>
    public HashSet<string> UsedSqlIdentifiers { get; } = [];

    /// <summary>
    /// The image the SQL Server container runs.
    /// </summary>
    public string SqlImage { get; private set; } = DockerWebDefaults.MsSqlImage;

    /// <summary>
    /// The <c>sa</c> password of the SQL Server container.
    /// </summary>
    public string SqlPassword { get; private set; } = DockerWebDefaults.MsSqlPassword;

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

        if (definition is DockerSqlDefinition sql)
        {
            DockerSqlDefinition? conflicting = GetSqlDefinitions()
                .FirstOrDefault(existing => existing.GetType() != sql.GetType() && string.Equals(existing.Identifier, sql.Identifier, StringComparison.Ordinal));

            if (conflicting is not null)
                throw new FrameworkConfigurationException($"'{sql.GetType().Name}' and '{conflicting.GetType().Name}' both declare the SQL identifier '{sql.Identifier}'. One identifier is served by one definition.");
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
    /// The SQL databases this environment provisions.
    /// </summary>
    public IReadOnlyList<DockerSqlDefinition> GetSqlDefinitions()
        => [.. _definitions.Values.OfType<DockerSqlDefinition>().OrderBy(definition => definition.Identifier.Identifier, StringComparer.Ordinal)];

    /// <inheritdoc />
    public override IReadOnlyCollection<EnvComponentIdentifier> ResolveComponents(IEnumerable<ArtifactInstanceGeneric> artifacts, IEnumerable<EnvironmentRequirement> requirements)
    {
        ArgumentNullException.ThrowIfNull(artifacts);

        UsedSqlIdentifiers.Clear();

        foreach (ArtifactInstanceGeneric artifact in artifacts)
        {
            // The reference states which database it belongs to, so no reflection over artifact
            // types is needed to find out.
            if (artifact.Reference is ISqlArtifactReference sqlReference)
                UsedSqlIdentifiers.Add(sqlReference.SqlIdentifier);
        }

        HashSet<EnvComponentIdentifier> resolved = [.. base.ResolveComponents(artifacts, requirements)];

        // A declared database is started whether or not this particular timeline touches it: it was
        // asked for, and one container serves all of them anyway.
        if (GetSqlDefinitions().Count > 0 || UsedSqlIdentifiers.Count > 0)
            resolved.Add(SqlServerComponentId);

        EnsureDeclaredIdentifiers();

        return [.. resolved];
    }

    /// <summary>
    /// Publishes the state a created component produced.
    /// </summary>
    /// <param name="identifier">The component that produced it.</param>
    /// <param name="state">The state.</param>
    public void SetRuntimeState(EnvComponentIdentifier identifier, object? state)
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
    public TState GetRequiredRuntimeState<TState>(EnvComponentIdentifier identifier)
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
            UsedSqlIdentifiers.Add(requirement.ResourceIdentifier);
    }

    private void EnsureDeclaredIdentifiers()
    {
        HashSet<string> declared = [.. GetSqlDefinitions().Select(definition => definition.Identifier.Identifier)];
        string[] missing = [.. UsedSqlIdentifiers.Where(identifier => !declared.Contains(identifier)).OrderBy(identifier => identifier, StringComparer.Ordinal)];
        if (missing.Length == 0)
            return;

        throw new FrameworkConfigurationException(
            $"The run uses the SQL identifier(s) {string.Join(", ", missing.Select(identifier => $"'{identifier}'"))}, which no included definition declares. "
            + $"Declared: {(declared.Count == 0 ? "none" : string.Join(", ", declared.OrderBy(identifier => identifier, StringComparer.Ordinal)))}. "
            + "Include a DockerSqlDefinition for it, so the environment knows which database to create.");
    }
}
