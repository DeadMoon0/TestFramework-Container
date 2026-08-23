using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Web;

/// <summary>
/// One proxied location in a generated site server configuration.
/// </summary>
/// <param name="LocationPath">The path prefix the site serves, for example <c>/api</c>.</param>
/// <param name="Target">The address the traffic is forwarded to.</param>
/// <param name="StripPrefix">Whether the matched prefix is removed before forwarding.</param>
/// <param name="Description">What the route points at, named in a generated comment.</param>
public sealed record NginxProxyLocation(string LocationPath, Uri Target, bool StripPrefix, string Description);

/// <summary>
/// Builds the server configuration a site container serves its payload with.
/// </summary>
/// <remarks>
/// The file is generated rather than templated by the caller for the same reason the API settings
/// file is: the exact configuration the server loaded is inspectable in the run's log, and the two
/// classic nginx traps -- the trailing-slash semantics of <c>proxy_pass</c> and the missing SPA
/// history fallback -- are encoded once, with their rules stated in generated comments.
/// </remarks>
public static class NginxConfigFile
{
    /// <summary>
    /// The file name the configuration is written as.
    /// </summary>
    public static string FileName => "default.conf";

    /// <summary>
    /// Composes the server block.
    /// </summary>
    /// <param name="listenPort">The port the server listens on inside the container.</param>
    /// <param name="spaFallback">Whether unknown paths fall back to <c>index.html</c>.</param>
    /// <param name="proxyRoutes">The proxied locations, in declaration order.</param>
    /// <param name="extraDirectives">Raw directives appended verbatim inside the server block.</param>
    public static string Compose(
        int listenPort,
        bool spaFallback,
        IReadOnlyList<NginxProxyLocation> proxyRoutes,
        IReadOnlyList<string> extraDirectives)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(listenPort);
        ArgumentNullException.ThrowIfNull(proxyRoutes);
        ArgumentNullException.ThrowIfNull(extraDirectives);

        StringBuilder builder = new();
        builder.Append("server {\n");
        builder.Append($"    listen {listenPort};\n");
        builder.Append("    server_name _;\n");
        builder.Append('\n');
        builder.Append($"    root {DockerWebDefaults.SiteContentRoot};\n");
        builder.Append("    index index.html;\n");

        foreach (NginxProxyLocation route in proxyRoutes)
            AppendProxyLocation(builder, route);

        foreach (string directive in extraDirectives)
        {
            builder.Append('\n');
            builder.Append("    # declared by the definition, verbatim\n");
            builder.Append($"    {directive.Trim()}\n");
        }

        builder.Append('\n');
        if (spaFallback)
        {
            builder.Append("    # SPA history fallback: unknown paths are the application's own routes\n");
            builder.Append("    location / {\n");
            builder.Append("        try_files $uri $uri/ /index.html;\n");
            builder.Append("    }\n");
        }
        else
        {
            builder.Append("    location / {\n");
            builder.Append("        try_files $uri $uri/ =404;\n");
            builder.Append("    }\n");
        }

        builder.Append("}\n");
        return builder.ToString();
    }

    /// <summary>
    /// Returns the file content as the bytes copied into the container.
    /// </summary>
    /// <param name="config">The composed file content.</param>
    public static byte[] ToBytes(string config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(config);
        return Encoding.UTF8.GetBytes(config);
    }

    /// <summary>
    /// Normalizes a declared location path: it must start with <c>/</c> and is compared without its
    /// trailing slash.
    /// </summary>
    /// <param name="locationPath">The declared path.</param>
    /// <exception cref="FrameworkConfigurationException">The path does not start with <c>/</c>.</exception>
    public static string NormalizeLocationPath(string locationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locationPath);
        if (!locationPath.StartsWith('/'))
            throw new FrameworkConfigurationException($"The proxy location '{locationPath}' does not start with '/'. A location is a path prefix on the site's own origin.");

        string trimmed = locationPath.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    private static void AppendProxyLocation(StringBuilder builder, NginxProxyLocation route)
    {
        string prefix = NormalizeLocationPath(route.LocationPath);

        // The URI part of proxy_pass is the whole rule: with none, nginx forwards the original path
        // unchanged; with '/', it replaces the matched prefix. Both generated lines say which they do.
        string target = route.Target.ToString().TrimEnd('/');
        string proxyPass = route.StripPrefix
            ? $"proxy_pass {target}/;          # URI part '/': the '{prefix}/' prefix is replaced"
            : $"proxy_pass {target};           # no URI part: the original path is forwarded unchanged";

        builder.Append('\n');
        builder.Append($"    # generated proxy route for {route.Description}\n");
        builder.Append($"    location = {prefix} {{ return 301 {prefix}/; }}\n");
        builder.Append($"    location {prefix}/ {{\n");
        builder.Append($"        {proxyPass}\n");
        builder.Append("        proxy_http_version 1.1;\n");
        builder.Append("        proxy_set_header Host $host;\n");
        builder.Append("        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;\n");
        builder.Append("        proxy_set_header X-Forwarded-Proto $scheme;\n");
        builder.Append("    }\n");
    }
}
