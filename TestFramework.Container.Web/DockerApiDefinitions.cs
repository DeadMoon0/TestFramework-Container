using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TestFramework.Core.Exceptions;
using TestFramework.Web.Identifier;
using TestFramework.Web.Sql;

namespace TestFramework.Container.Web;

/// <summary>
/// Declares an application served by a container the environment starts.
/// </summary>
/// <typeparam name="TEntryPoint">
/// Any type from the application assembly. It names the assembly whose build output is shipped, so
/// a public marker type in the application project is the least intrusive choice: the generated
/// <c>Program</c> of a minimal-hosting application is internal and cannot be used here.
/// </typeparam>
/// <example>
/// <code>
/// // in the application project
/// public sealed class OrdersApiMarker;
///
/// // in the test project
/// internal sealed class OrdersApiDefinition : DockerApiDefinition&lt;OrdersApiMarker&gt;
/// {
///     public override ApiIdentifier Identifier =&gt; "orders";
///
///     protected override void Configure(DockerApiBuilder builder) =&gt; builder
///         .WithHealthPath("/health")
///         .UseSql&lt;SalesSqlDefinition&gt;("ConnectionStrings:Sales");
/// }
/// </code>
/// </example>
public abstract class DockerApiDefinition<TEntryPoint> : DockerApiDefinition
    where TEntryPoint : class
{
    /// <inheritdoc />
    public sealed override Type EntryPointType => typeof(TEntryPoint);
}

/// <summary>
/// Declares an application served by a container the environment starts.
/// </summary>
public abstract class DockerApiDefinition : DockerWebDefinition
{
    /// <summary>
    /// The API identifier timelines use to reach this application.
    /// </summary>
    public abstract ApiIdentifier Identifier { get; }

    /// <summary>
    /// A type from the application assembly, used to find its build output.
    /// </summary>
    public abstract Type EntryPointType { get; }

    /// <summary>
    /// Declares the settings, dependencies and readiness of the application.
    /// </summary>
    /// <param name="builder">The builder collecting the declaration.</param>
    protected abstract void Configure(DockerApiBuilder builder);

    /// <summary>
    /// Builds the declaration this definition describes.
    /// </summary>
    /// <exception cref="FrameworkConfigurationException">The declaration is inconsistent.</exception>
    public DockerApiSpec Build()
    {
        DockerApiBuilder builder = new(GetType());
        Configure(builder);
        return builder.Build();
    }
}

/// <summary>
/// Collects how one application is run.
/// </summary>
/// <param name="definitionType">The definition being configured, named in error messages.</param>
public sealed class DockerApiBuilder(Type definitionType)
{
    private readonly Dictionary<string, string> _settings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _environmentVariables = new(StringComparer.Ordinal);
    private readonly List<DockerApiSqlBinding> _sqlBindings = [];
    private string _environmentName = DockerWebDefaults.ApiEnvironmentName;
    private string? _healthPath = DockerWebDefaults.ApiHealthPath;
    private string? _image;
    private string? _outputDirectory;
    private int _internalPort = DockerWebDefaults.ApiInternalPort;
    private TimeSpan _readinessTimeout = DockerWebDefaults.ApiReadinessTimeout;

    /// <summary>
    /// Sets the hosting environment name, which selects the generated settings file.
    /// </summary>
    /// <param name="environmentName">The environment name, for example <c>Testing</c>.</param>
    public DockerApiBuilder WithEnvironmentName(string environmentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        _environmentName = environmentName;
        return this;
    }

    /// <summary>
    /// Sets the path probed until the application answers, and reported to liveness steps.
    /// </summary>
    /// <param name="healthPath">The path, relative to the base address.</param>
    public DockerApiBuilder WithHealthPath(string healthPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(healthPath);
        _healthPath = healthPath.StartsWith('/') ? healthPath : $"/{healthPath}";
        return this;
    }

    /// <summary>
    /// Accepts any HTTP answer as readiness, for an application with no health endpoint.
    /// </summary>
    public DockerApiBuilder WithoutHealthCheck()
    {
        _healthPath = null;
        return this;
    }

    /// <summary>
    /// Overrides the runtime image. By default it follows the framework the application was built for.
    /// </summary>
    /// <param name="image">The image to run.</param>
    public DockerApiBuilder WithImage(string image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        _image = image;
        return this;
    }

    /// <summary>
    /// Ships an explicit directory instead of the one resolved from the entry point assembly.
    /// </summary>
    /// <param name="outputDirectory">The directory holding the built application.</param>
    /// <remarks>
    /// Resolution walks up from the loaded assembly to find the owning project, which is convenient
    /// but not obvious. This is the way to say exactly what gets shipped.
    /// </remarks>
    public DockerApiBuilder WithOutputDirectory(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        _outputDirectory = outputDirectory;
        return this;
    }

    /// <summary>
    /// Sets the port the application listens on inside the container.
    /// </summary>
    /// <param name="internalPort">The container port.</param>
    public DockerApiBuilder WithPort(int internalPort)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(internalPort);
        _internalPort = internalPort;
        return this;
    }

    /// <summary>
    /// Sets how long to wait for the application to answer before failing the setup.
    /// </summary>
    /// <param name="readinessTimeout">The readiness timeout.</param>
    public DockerApiBuilder WithReadinessTimeout(TimeSpan readinessTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(readinessTimeout, TimeSpan.Zero);
        _readinessTimeout = readinessTimeout;
        return this;
    }

    /// <summary>
    /// Adds a configuration value, written to the generated settings file.
    /// </summary>
    /// <param name="path">The configuration path, colon-separated, for example <c>Features:UseFakeClock</c>.</param>
    /// <param name="value">The value.</param>
    public DockerApiBuilder WithSetting(string path, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(value);
        _settings[path] = value;
        return this;
    }

    /// <summary>
    /// Adds an environment variable, for the values that really are environment variables.
    /// </summary>
    /// <param name="name">The variable name.</param>
    /// <param name="value">The value.</param>
    public DockerApiBuilder WithEnvironmentVariable(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        _environmentVariables[name] = value;
        return this;
    }

    /// <summary>
    /// Points a configuration value at a declared database, using the address containers reach it by.
    /// </summary>
    /// <typeparam name="TDefinition">The database definition to bind to.</typeparam>
    /// <param name="settingPath">The configuration path receiving the connection string.</param>
    /// <remarks>
    /// The application is given the network address of the database, never the host-mapped one the
    /// test process uses. Getting that backwards is the classic container-setup failure.
    /// </remarks>
    public DockerApiBuilder UseSql<TDefinition>(string settingPath)
        where TDefinition : DockerSqlDefinition, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingPath);
        _sqlBindings.Add(new DockerApiSqlBinding(new TDefinition().Identifier, settingPath));
        return this;
    }

    /// <summary>
    /// Validates and returns the collected declaration.
    /// </summary>
    /// <exception cref="FrameworkConfigurationException">The declaration is inconsistent.</exception>
    public DockerApiSpec Build()
    {
        string[] collisions = [.. _sqlBindings
            .Select(binding => binding.SettingPath)
            .Where(path => _settings.ContainsKey(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        if (collisions.Length > 0)
            throw new FrameworkConfigurationException($"'{definitionType.Name}' sets {string.Join(", ", collisions.Select(path => $"'{path}'"))} both directly and from a database binding, so the value that would win is not obvious. Remove one of the two.");

        string[] duplicateBindings = [.. _sqlBindings
            .GroupBy(binding => binding.SettingPath, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)];

        if (duplicateBindings.Length > 0)
            throw new FrameworkConfigurationException($"'{definitionType.Name}' binds {string.Join(", ", duplicateBindings.Select(path => $"'{path}'"))} to more than one database.");

        return new DockerApiSpec(
            _image,
            _outputDirectory,
            _environmentName,
            _healthPath,
            _internalPort,
            _readinessTimeout,
            _settings,
            _environmentVariables,
            [.. _sqlBindings]);
    }
}

/// <summary>
/// A configuration value that carries the address of a declared database.
/// </summary>
/// <param name="SqlIdentifier">The database the value points at.</param>
/// <param name="SettingPath">The configuration path receiving the connection string.</param>
public sealed record DockerApiSqlBinding(SqlIdentifier SqlIdentifier, string SettingPath);

/// <summary>
/// How one application is run.
/// </summary>
/// <param name="Image">An explicit runtime image, or <see langword="null"/> to follow the built framework.</param>
/// <param name="OutputDirectory">An explicit build output directory, or <see langword="null"/> to resolve it.</param>
/// <param name="EnvironmentName">The hosting environment name.</param>
/// <param name="HealthPath">The readiness path, or <see langword="null"/> to accept any HTTP answer.</param>
/// <param name="InternalPort">The port the application listens on inside the container.</param>
/// <param name="ReadinessTimeout">How long to wait for the application to answer.</param>
/// <param name="Settings">Configuration values written to the generated settings file.</param>
/// <param name="EnvironmentVariables">Environment variables passed to the container.</param>
/// <param name="SqlBindings">Configuration values carrying database addresses.</param>
public sealed record DockerApiSpec(
    string? Image,
    string? OutputDirectory,
    string EnvironmentName,
    string? HealthPath,
    int InternalPort,
    TimeSpan ReadinessTimeout,
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    IReadOnlyList<DockerApiSqlBinding> SqlBindings)
{
    /// <summary>
    /// Returns the runtime image for a target framework moniker.
    /// </summary>
    /// <param name="targetFramework">The moniker the application was built for, such as <c>net10.0</c>.</param>
    /// <remarks>
    /// The image follows the application rather than being fixed, so an application that moves to a
    /// newer framework does not silently run on an older runtime.
    /// </remarks>
    public string ResolveImage(string targetFramework)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFramework);

        if (!string.IsNullOrWhiteSpace(Image))
            return Image;

        if (!targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase)
            || !double.TryParse(targetFramework[3..], NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            throw new FrameworkConfigurationException(
                $"No runtime image can be derived from the target framework '{targetFramework}'. Call WithImage(\"...\") to name one.");
        }

        return $"{DockerWebDefaults.AspNetImageRepository}:{targetFramework[3..]}";
    }

    /// <summary>
    /// Returns a readable description of the declaration.
    /// </summary>
    public override string ToString()
        => $"{EnvironmentName} on port {InternalPort} ({Settings.Count} setting(s), {SqlBindings.Count} database binding(s))";
}
