using System;

namespace TestFramework.Container.Web;

/// <summary>
/// Which settings file an application container reads its configuration from.
/// </summary>
/// <remarks>
/// <para>
/// Configuration reaches the application as a generated <c>appsettings.&lt;Environment&gt;.json</c>
/// rather than as environment variables, because a file can be read back. When a run behaves
/// unexpectedly the exact file the application loaded is inspectable, which a set of doubly
/// underscored variables is not.
/// </para>
/// <para>
/// Composing that file is not this type's job: <c>JsonPathDocument</c> in Core does it, because an API's
/// settings, a site's configuration file and a function app's settings are the same problem three times.
/// This is only the name.
/// </para>
/// </remarks>
public static class ApiSettingsFile
{
    /// <summary>
    /// Returns the file name the hosting environment causes to be loaded.
    /// </summary>
    /// <param name="environmentName">The hosting environment name.</param>
    public static string FileName(string environmentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        return $"appsettings.{environmentName}.json";
    }
}
