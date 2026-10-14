using Xunit;

namespace TestFramework.Container.Web.Tests;

/// <summary>
/// The cases that start more than one SQL Server, run on their own.
/// </summary>
/// <remarks>
/// Each SQL Server takes up to <see cref="DockerWebDefaults.MsSqlMemoryLimitMb"/> megabytes, and the smoke suite
/// already starts a burst of them per target framework at once. Stacking these on that burst overcommitted the
/// Docker VM: servers were killed mid-boot and the daemon answered 500 - failures in whichever test happened to be
/// starting, not in the one that caused them.
/// </remarks>
[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class SeveralSqlServersCollection
{
    public const string CollectionName = "Several SQL Servers";
}
