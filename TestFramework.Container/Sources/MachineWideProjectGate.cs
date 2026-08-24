using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TestFramework.Container.Sources;

/// <summary>
/// A machine-wide lock per project, for the contention the in-process gates cannot see.
/// </summary>
/// <remarks>
/// <para>
/// The per-project <see cref="SemaphoreSlim"/> serializes the environments of one test process. A
/// multi-targeted test suite, however, runs one process per framework at the same time, and each
/// carries its own semaphores - so two processes publishing the same project still drive MSBuild
/// over one <c>obj/</c> directory at once, which is exactly the race the in-process gate exists to
/// prevent. A Function App makes it certain rather than likely: its worker SDK generates a nested
/// WorkerExtensions build inside that directory, and the loser dies writing the deps file.
/// </para>
/// <para>
/// This lock closes the gap with the one arbiter every process shares: the file system. Holding is
/// what locks - the lock file is opened with no sharing, and whichever process has it open makes
/// everyone else wait. The file lives in the temp directory, keyed on a hash of the project path
/// rather than the path itself, so no lock file ever appears next to anyone's sources; it is left
/// behind empty afterwards, because existing does not lock.
/// </para>
/// </remarks>
internal static class MachineWideProjectGate
{
    /// <summary>
    /// How long a waiter sleeps between attempts. A publish takes seconds, so a quarter of one is
    /// close enough to immediate without hammering the file system.
    /// </summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Takes the machine-wide lock for a project, waiting for whichever process holds it.
    /// </summary>
    /// <param name="projectPath">The project file the lock is for.</param>
    /// <param name="cancellationToken">Cancels the waiting.</param>
    /// <returns>The held lock; disposing it lets the next waiter in.</returns>
    public static async Task<IDisposable> EnterAsync(string projectPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        string lockPath = LockPathFor(projectPath);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
            }
            catch (IOException)
            {
                // Another process is publishing this project right now. Waiting for it is the point.
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Where the lock file for a project lives.
    /// </summary>
    /// <param name="projectPath">The project file.</param>
    /// <returns>The lock file path, the same one from every process on this machine.</returns>
    internal static string LockPathFor(string projectPath)
    {
        string normalized = Path.GetFullPath(projectPath);

        if (OperatingSystem.IsWindows())
        {
            // The lock must not split over spelling on a file system that does not.
            normalized = normalized.ToUpperInvariant();
        }

        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));

        return Path.Combine(Path.GetTempPath(), "testframework-container-gates", key + ".lock");
    }
}
