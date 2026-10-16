using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Azure.Identifier;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Azure;

/// <summary>
/// A SQL database served by the SQL Server container.
/// </summary>
/// <remarks>
/// When <see cref="DatabaseName"/> is set, the definition declares a SQL database configuration for
/// <see cref="Identifier"/> to the run; its connection string is replaced by the address SQL Server publishes.
/// </remarks>
public abstract class DockerSqlDatabaseDefinition : DockerAzureDefinition
{
    /// <summary>
    /// The SQL database identifier steps and configuration refer to.
    /// </summary>
    public abstract SqlDatabaseIdentifier Identifier { get; }

    /// <summary>
    /// The database name declared in the configuration, or <c>null</c> to declare no configuration.
    /// </summary>
    protected virtual string? DatabaseName => null;

    internal SqlDatabaseConfig BuildConfig(string connectionString) => new()
    {
        ConnectionString = connectionString,
        DatabaseName = DatabaseName ?? throw new FrameworkConfigurationException($"Override '{nameof(DatabaseName)}' on '{GetType().Name}' to provide a SQL database name."),
    };

    internal bool TryCreateDefaultConfig(out SqlDatabaseConfig config)
    {
        if (DatabaseName is null)
        {
            config = default!;
            return false;
        }

        config = BuildConfig(DockerAzureDefaults.PlaceholderConnectionString);
        return true;
    }
}
