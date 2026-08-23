using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Web;

/// <summary>
/// Builds the runtime configuration file a site's browser code fetches.
/// </summary>
/// <remarks>
/// The bound values are merged over the file the payload already ships: a checked-in
/// <c>config.json</c> keeps every field the run does not bind, so a definition overrides one
/// address without rebuilding the application's whole configuration shape.
/// </remarks>
public static class SiteConfigFile
{
    /// <summary>
    /// Composes the file by merging bound values over the payload's existing content.
    /// </summary>
    /// <param name="existingJson">The file as the payload ships it, or <see langword="null"/> when absent.</param>
    /// <param name="values">The bound values, keyed by colon-separated path.</param>
    /// <exception cref="FrameworkConfigurationException">The existing content is not a JSON object, or paths conflict.</exception>
    /// <example>
    /// <c>backend:orders</c> becomes <c>{ "backend": { "orders": "..." } }</c>.
    /// </example>
    public static string Compose(string? existingJson, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        JsonObject root = ParseExisting(existingJson);

        // Ordered so the generated file is stable between runs and readable in a diff.
        foreach ((string path, string value) in values.OrderBy(setting => setting.Key, StringComparer.Ordinal))
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

    private static JsonObject ParseExisting(string? existingJson)
    {
        if (string.IsNullOrWhiteSpace(existingJson))
            return [];

        try
        {
            return JsonNode.Parse(existingJson) as JsonObject
                ?? throw new FrameworkConfigurationException("The site's existing configuration file is valid JSON but not an object, so there is nothing to merge bound values into.");
        }
        catch (JsonException exception)
        {
            throw new FrameworkConfigurationException(
                "The site's existing configuration file is not valid JSON, so bound values cannot be merged into it.",
                ["Fix the checked-in file, or compose the whole file with WithConfigFile(...) instead."],
                null,
                exception);
        }
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
            if (current.TryGetPropertyValue(segment, out JsonNode? existing) && existing is JsonObject child)
            {
                current = child;
                continue;
            }

            // A leaf in the way is replaced by a section: the bound path was declared, the shipped
            // value was not.
            JsonObject created = [];
            current[segment] = created;
            current = created;
        }

        current[segments[^1]] = value;
    }
}
