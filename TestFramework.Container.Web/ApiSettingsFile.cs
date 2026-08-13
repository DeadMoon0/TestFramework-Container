using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Web;

/// <summary>
/// Builds the settings file an application container reads its configuration from.
/// </summary>
/// <remarks>
/// Configuration reaches the application as a generated <c>appsettings.&lt;Environment&gt;.json</c>
/// rather than as environment variables, because a file can be read back. When a run behaves
/// unexpectedly the exact file the application loaded is inspectable, which a set of doubly
/// underscored variables is not.
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

    /// <summary>
    /// Composes indented JSON from colon-separated configuration paths.
    /// </summary>
    /// <param name="settings">The configuration values, keyed by path.</param>
    /// <exception cref="FrameworkConfigurationException">One path is nested inside another.</exception>
    /// <example>
    /// <c>ConnectionStrings:Sales</c> becomes <c>{ "ConnectionStrings": { "Sales": "..." } }</c>.
    /// </example>
    public static string Compose(IReadOnlyDictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        JsonObject root = [];

        // Ordered so the generated file is stable between runs and readable in a diff.
        foreach ((string path, string value) in settings.OrderBy(setting => setting.Key, StringComparer.Ordinal))
            Insert(root, path, value);

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Returns the file content as the bytes copied into the container.
    /// </summary>
    /// <param name="json">The composed file content.</param>
    public static byte[] ToBytes(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return Encoding.UTF8.GetBytes(json);
    }

    private static void Insert(JsonObject root, string path, string value)
    {
        string[] segments = path.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
            throw new FrameworkConfigurationException($"The configuration path '{path}' is empty.");

        JsonObject current = root;
        for (int index = 0; index < segments.Length - 1; index++)
        {
            string segment = segments[index];
            if (!current.TryGetPropertyValue(segment, out JsonNode? existing))
            {
                JsonObject created = [];
                current[segment] = created;
                current = created;
                continue;
            }

            if (existing is not JsonObject child)
                throw ConflictingPaths(path, string.Join(':', segments.Take(index + 1)));

            current = child;
        }

        string leaf = segments[^1];
        if (current.TryGetPropertyValue(leaf, out JsonNode? leafNode) && leafNode is JsonObject)
            throw ConflictingPaths(path, path);

        current[leaf] = value;
    }

    private static FrameworkConfigurationException ConflictingPaths(string path, string conflictingPrefix)
        => new($"The configuration path '{path}' cannot be set, because '{conflictingPrefix}' is already used as a section. Two settings are nested inside each other.");
}
