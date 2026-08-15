namespace TestFramework.Container.Azure;

/// <summary>
/// What happens to the emulators a previous run already used when the next run starts.
/// </summary>
/// <remarks>
/// <para>
/// The hosted fixture keeps the emulator containers alive across the whole test collection, which is
/// where its one to three minutes of saving comes from. Their contents survive with them: blobs, table
/// rows, Cosmos items, queue messages and Service Bus messages written by one run are still there for
/// the next one. That is the opposite of what a test run is entitled to assume.
/// </para>
/// <para>
/// Purging costs seconds against the minutes the fixture saves, so it is the default. The other mode
/// exists for a suite that deliberately builds state across runs, and for measuring what the purge
/// itself costs.
/// </para>
/// </remarks>
public enum AzureResetMode
{
    /// <summary>
    /// Empty every resource the environment declared, before anything is started against it.
    /// </summary>
    /// <remarks>
    /// Only declared resources are touched: the storage account, Cosmos container, Service Bus entities
    /// and SQL databases named by the environment's own configuration. Nothing else on the emulators is
    /// looked at, and the Service Bus emulator's own database is never one of them.
    /// </remarks>
    PurgeDeclaredResources,

    /// <summary>
    /// Leave whatever the previous run wrote in place.
    /// </summary>
    None,
}
