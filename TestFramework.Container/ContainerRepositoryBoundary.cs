using System;
using System.IO;
using System.Linq;

namespace TestFramework.Container;

/// <summary>
/// Decides where a directory walk that climbs towards the drive root should stop.
/// </summary>
/// <remarks>
/// Two places in this framework look for a file by climbing ancestors: locating the project that
/// produced a build output, and locating a Service Bus topology file. Both went to the drive root when
/// they found nothing, which turns a missing file into a long scan and, worse, lets a coincidentally
/// named file outside the repository win. One rule, applied in both, ends the climb where the
/// repository does.
/// </remarks>
public static class ContainerRepositoryBoundary
{
    /// <summary>
    /// Whether a directory is the top of a repository or a solution.
    /// </summary>
    /// <param name="directory">The directory to test.</param>
    /// <remarks>
    /// <c>.git</c> is a directory in an ordinary clone and a file in a worktree or a submodule, so both
    /// count. A solution file or a <c>global.json</c> marks the same boundary for a checkout that has no
    /// <c>.git</c> of its own.
    /// </remarks>
    public static bool IsBoundary(DirectoryInfo directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        try
        {
            string gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return true;

            if (File.Exists(Path.Combine(directory.FullName, "global.json")))
                return true;

            return directory.EnumerateFiles("*.sln").Any() || directory.EnumerateFiles("*.slnx").Any();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            // A directory that cannot be read is not evidence of a boundary.
            return false;
        }
    }
}
