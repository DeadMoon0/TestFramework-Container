namespace TestFramework.Container.Web;

/// <summary>
/// What happens to a database that already exists when a run starts.
/// </summary>
/// <remarks>
/// A container can outlive a single run, so the state a previous run left behind is a real concern.
/// The cheapest mode that keeps a test honest is the right one: recreating a database costs seconds
/// on every run, while leaving it dirty makes a test pass for the wrong reason.
/// </remarks>
public enum SqlResetMode
{
    /// <summary>
    /// Create the database when it is missing, and leave whatever it already contains.
    /// </summary>
    None,

    /// <summary>
    /// Run the configured reset script after the schema is in place, to clear data between runs.
    /// </summary>
    RunResetScript,

    /// <summary>
    /// Drop the database and create it again, so every run starts from the declared schema alone.
    /// </summary>
    RecreateDatabase,
}
