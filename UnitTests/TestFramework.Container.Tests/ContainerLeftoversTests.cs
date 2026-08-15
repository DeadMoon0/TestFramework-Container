using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace TestFramework.Container.Tests;

/// <summary>
/// Covers the sweep's blast radius, which is the only thing about it that could do harm.
/// </summary>
public class ContainerLeftoversTests
{
    [Fact]
    public async Task SweepAsync_LeavesRecentAndForeignTemporaryDirectoriesAlone()
    {
        string recent = Path.Combine(Path.GetTempPath(), $"{ContainerLeftovers.TempPrefix}recent-{Guid.NewGuid():N}");
        string foreign = Path.Combine(Path.GetTempPath(), $"not-ours-{Guid.NewGuid():N}");
        string expired = Path.Combine(Path.GetTempPath(), $"{ContainerLeftovers.TempPrefix}expired-{Guid.NewGuid():N}");

        Directory.CreateDirectory(recent);
        Directory.CreateDirectory(foreign);
        Directory.CreateDirectory(expired);
        Directory.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-3));
        Directory.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddDays(-3));

        try
        {
            await ContainerLeftovers.SweepAsync(null, CancellationToken.None);

            Assert.True(Directory.Exists(recent), "A directory written today is still in use.");
            Assert.True(Directory.Exists(foreign), "Only the framework's own prefix may be swept.");
            Assert.False(Directory.Exists(expired), "A framework directory older than a day is litter.");
        }
        finally
        {
            foreach (string directory in new[] { recent, foreign, expired })
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SweepAsync_DoesNothingWhenTurnedOff()
    {
        string expired = Path.Combine(Path.GetTempPath(), $"{ContainerLeftovers.TempPrefix}optout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(expired);
        Directory.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-3));

        Environment.SetEnvironmentVariable(ContainerLeftovers.NoSweepVariable, "1");
        try
        {
            await ContainerLeftovers.SweepAsync(null, CancellationToken.None);

            Assert.True(Directory.Exists(expired));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ContainerLeftovers.NoSweepVariable, null);
            if (Directory.Exists(expired))
                Directory.Delete(expired, recursive: true);
        }
    }

    [Fact]
    public void TopologyDirectory_IsUnderTheFrameworksOwnTempFolder()
    {
        // Widening this past the framework's own directory is how housekeeping would start deleting
        // somebody else's files.
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "TestFramework"), ContainerLeftovers.TopologyDirectory, StringComparison.OrdinalIgnoreCase);
    }
}
