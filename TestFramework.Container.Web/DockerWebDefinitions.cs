using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using TestFramework.Core.Exceptions;
using TestFramework.Web.Sql;
using TestFramework.Web.Sql.Steps;

namespace TestFramework.Container.Web;

/// <summary>
/// Declares one resource a container-backed web environment provides.
/// </summary>
/// <remarks>
/// A definition is the test-side description of a resource: what it is called, what it contains and
/// how it is reset. It holds no runtime state, so the same definition can be reused across runs.
/// </remarks>
public abstract class DockerWebDefinition
{
}

/// <summary>
/// Declares a database served by the environment's SQL Server container.
/// </summary>
/// <example>
/// <code>
/// internal sealed class SampleSqlDefinition : DockerSqlDefinition
/// {
///     public override SqlIdentifier Identifier =&gt; "main";
///
///     protected override void Configure(DockerSqlBuilder builder) =&gt; builder
///         .WithDatabase("SampleDb")
///         .WithSchemaFromModels&lt;Order, Customer&gt;()
///         .WithResetMode(SqlResetMode.RecreateDatabase);
/// }
/// </code>
/// </example>
public abstract class DockerSqlDefinition : DockerWebDefinition
{
    /// <summary>
    /// The SQL identifier timelines use to reach this database.
    /// </summary>
    public abstract SqlIdentifier Identifier { get; }

    /// <summary>
    /// Declares the database name, its schema and how it is reset.
    /// </summary>
    /// <param name="builder">The builder collecting the declaration.</param>
    protected abstract void Configure(DockerSqlBuilder builder);

    /// <summary>
    /// Builds the declaration this definition describes.
    /// </summary>
    /// <exception cref="FrameworkConfigurationException">The declaration is incomplete or inconsistent.</exception>
    public DockerSqlSpec Build()
    {
        DockerSqlBuilder builder = new(GetType());
        Configure(builder);
        return builder.Build();
    }
}

/// <summary>
/// Collects what one database is made of.
/// </summary>
/// <param name="definitionType">The definition being configured, named in error messages.</param>
public sealed class DockerSqlBuilder(Type definitionType)
{
    private readonly List<Type> _modelTypes = [];
    private readonly List<SqlScript> _scripts = [];
    private string? _database;
    private SqlScript? _resetScript;
    private SqlResetMode _resetMode = SqlResetMode.None;

    /// <summary>
    /// Sets the name of the database to create.
    /// </summary>
    /// <param name="database">The database name.</param>
    public DockerSqlBuilder WithDatabase(string database)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        _database = database;
        return this;
    }

    /// <summary>
    /// Derives the tables from a registered model.
    /// </summary>
    /// <typeparam name="TModel">The model type.</typeparam>
    /// <remarks>
    /// The mapping is resolved from the run's own model registry, so the fluent registrations a test
    /// makes with <c>AddWebSqlModels</c> shape the generated tables.
    /// </remarks>
    public DockerSqlBuilder WithSchemaFromModels<TModel>()
        where TModel : class
        => WithSchemaFromModels(typeof(TModel));

    /// <summary>
    /// Derives the tables from two registered models.
    /// </summary>
    /// <typeparam name="TFirst">The first model type.</typeparam>
    /// <typeparam name="TSecond">The second model type.</typeparam>
    public DockerSqlBuilder WithSchemaFromModels<TFirst, TSecond>()
        where TFirst : class
        where TSecond : class
        => WithSchemaFromModels(typeof(TFirst), typeof(TSecond));

    /// <summary>
    /// Derives the tables from three registered models.
    /// </summary>
    /// <typeparam name="TFirst">The first model type.</typeparam>
    /// <typeparam name="TSecond">The second model type.</typeparam>
    /// <typeparam name="TThird">The third model type.</typeparam>
    public DockerSqlBuilder WithSchemaFromModels<TFirst, TSecond, TThird>()
        where TFirst : class
        where TSecond : class
        where TThird : class
        => WithSchemaFromModels(typeof(TFirst), typeof(TSecond), typeof(TThird));

    /// <summary>
    /// Derives the tables from registered models.
    /// </summary>
    /// <param name="modelTypes">The model types, in creation order.</param>
    public DockerSqlBuilder WithSchemaFromModels(params Type[] modelTypes)
    {
        ArgumentNullException.ThrowIfNull(modelTypes);

        foreach (Type modelType in modelTypes)
        {
            ArgumentNullException.ThrowIfNull(modelType);
            if (!_modelTypes.Contains(modelType))
                _modelTypes.Add(modelType);
        }

        return this;
    }

    /// <summary>
    /// Adds a script that runs after the generated tables, in the order the scripts are added.
    /// </summary>
    /// <param name="script">The script to run.</param>
    /// <remarks>
    /// Generation covers tables, columns, keys and identities. Everything else a database needs --
    /// views, indexes, reference data -- belongs in a script.
    /// </remarks>
    public DockerSqlBuilder WithSchemaScript(SqlScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        _scripts.Add(script);
        return this;
    }

    /// <summary>
    /// Sets the script that <see cref="SqlResetMode.RunResetScript"/> runs once the schema is in place.
    /// </summary>
    /// <param name="script">The reset script.</param>
    public DockerSqlBuilder WithResetScript(SqlScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        _resetScript = script;
        return this;
    }

    /// <summary>
    /// Sets what happens to a database a previous run left behind.
    /// </summary>
    /// <param name="resetMode">The reset mode.</param>
    public DockerSqlBuilder WithResetMode(SqlResetMode resetMode)
    {
        _resetMode = resetMode;
        return this;
    }

    /// <summary>
    /// Validates and returns the collected declaration.
    /// </summary>
    /// <exception cref="FrameworkConfigurationException">The declaration is incomplete or inconsistent.</exception>
    public DockerSqlSpec Build()
    {
        if (string.IsNullOrWhiteSpace(_database))
            throw new FrameworkConfigurationException($"'{definitionType.Name}' does not name a database. Call WithDatabase(\"...\") in Configure.");

        if (!DockerSqlSpec.IsValidDatabaseName(_database))
            throw new FrameworkConfigurationException($"'{definitionType.Name}' declares the database name '{_database}', which is not a plain identifier. Use letters, digits and underscores, starting with a letter or underscore.");

        if (_resetMode == SqlResetMode.RunResetScript && _resetScript is null)
            throw new FrameworkConfigurationException($"'{definitionType.Name}' selects {nameof(SqlResetMode)}.{nameof(SqlResetMode.RunResetScript)} without a reset script. Call WithResetScript(...), or choose another reset mode.");

        return new DockerSqlSpec(_database, [.. _modelTypes], [.. _scripts], _resetScript, _resetMode);
    }
}

/// <summary>
/// What one database is made of.
/// </summary>
/// <param name="DatabaseName">The database to create.</param>
/// <param name="ModelTypes">The models whose tables are generated.</param>
/// <param name="Scripts">The scripts applied after the generated tables.</param>
/// <param name="ResetScript">The script <see cref="SqlResetMode.RunResetScript"/> runs.</param>
/// <param name="ResetMode">What happens to a database that already exists.</param>
public sealed record DockerSqlSpec(
    string DatabaseName,
    IReadOnlyList<Type> ModelTypes,
    IReadOnlyList<SqlScript> Scripts,
    SqlScript? ResetScript,
    SqlResetMode ResetMode)
{
    /// <summary>
    /// Whether a database name is a plain identifier and therefore safe to place in a statement.
    /// </summary>
    /// <param name="database">The database name to check.</param>
    /// <remarks>
    /// SQL Server does not accept a parameter where a database name goes, so the name is validated
    /// instead of escaped.
    /// </remarks>
    public static bool IsValidDatabaseName(string database)
        => !string.IsNullOrWhiteSpace(database)
        && Regex.IsMatch(database, "^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Returns a readable description of the declaration.
    /// </summary>
    public override string ToString()
        => $"{DatabaseName} ({ModelTypes.Count} model(s), {Scripts.Count} script(s), reset {ResetMode})";
}
