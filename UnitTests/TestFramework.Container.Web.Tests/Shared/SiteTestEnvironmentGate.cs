using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Container.Web.Sites;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Web.Tests.Shared;

/// <summary>
/// Locates the site fixtures and decides what the current machine can run.
/// </summary>
/// <remarks>
/// The static fixture is checked in and always present. The Angular fixture is a plain folder next
/// to the test projects, built manually or by the pipeline (<c>npm ci &amp;&amp; npm run build</c>);
/// tests that need its output skip with a reason when it is absent, mirroring the browser suite's
/// gate in TestFramework-UI.
/// </remarks>
internal static class SiteTestEnvironmentGate
{
    /// <summary>
    /// The checked-in static site fixture.
    /// </summary>
    public static string StaticSiteDirectory => Path.Combine(UnitTestsRoot, "TestFramework.Container.Web.SampleSite.Static");

    /// <summary>
    /// The Angular fixture project.
    /// </summary>
    public static string AngularProjectDirectory => Path.Combine(UnitTestsRoot, "TestFramework.Container.Web.SampleSite");

    /// <summary>
    /// The Angular fixture's build output.
    /// </summary>
    public static string AngularDistDirectory => Path.Combine(AngularProjectDirectory, "dist", "sample-site", "browser");

    /// <summary>
    /// Returns why a site cannot be built with npm on this host, or <see langword="null"/> when it can.
    /// </summary>
    /// <remarks>
    /// Asked of npm, through the same resolution the host build uses, rather than of an opt-in variable: a flag
    /// decided whether the test ran, so a machine with npm skipped it silently and a machine without it was told
    /// to set a variable that could not help.
    /// </remarks>
    public static async Task<string?> HostNpmSkipReasonAsync()
    {
        try
        {
            NpmCliResult result = await NpmCli.RunAsync(["--version"], Path.GetTempPath(), TimeSpan.FromSeconds(30), CancellationToken.None);
            return result.Succeeded ? null : $"npm is on the PATH and did not answer: {result.Describe()}";
        }
        catch (FrameworkConfigurationException missing)
        {
            return missing.Message;
        }
    }

    /// <summary>
    /// Returns why the Angular-dist tests cannot run, or <see langword="null"/> when they can.
    /// </summary>
    public static string? AngularSkipReason()
        => File.Exists(Path.Combine(AngularDistDirectory, "index.html"))
            ? null
            : $"The Angular fixture is not built. Run 'npm ci && npm run build' in '{AngularProjectDirectory}'.";

    private static string UnitTestsRoot
    {
        get
        {
            DirectoryInfo? current = new(AppContext.BaseDirectory);
            while (current is not null && !string.Equals(current.Name, "UnitTests", StringComparison.OrdinalIgnoreCase))
                current = current.Parent;

            return current?.FullName
                ?? throw new InvalidOperationException($"No 'UnitTests' directory lies above '{AppContext.BaseDirectory}'.");
        }
    }
}
