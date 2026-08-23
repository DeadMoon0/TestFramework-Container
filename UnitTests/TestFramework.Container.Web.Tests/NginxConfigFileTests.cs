using System;
using TestFramework.Core.Exceptions;
using Xunit;

namespace TestFramework.Container.Web.Tests;

/// <summary>
/// Pins the generated server configuration to exact text: the proxy_pass trailing-slash semantics
/// and the SPA fallback are the two classic nginx traps, and the generator is where they are
/// encoded once.
/// </summary>
public class NginxConfigFileTests
{
    [Fact]
    public void Compose_WithNoRoutes_ServesThePayloadWithFallback()
    {
        string config = NginxConfigFile.Compose(80, spaFallback: true, [], []);

        Assert.Equal(
            "server {\n"
            + "    listen 80;\n"
            + "    server_name _;\n"
            + "\n"
            + "    root /usr/share/nginx/html;\n"
            + "    index index.html;\n"
            + "\n"
            + "    # SPA history fallback: unknown paths are the application's own routes\n"
            + "    location / {\n"
            + "        try_files $uri $uri/ /index.html;\n"
            + "    }\n"
            + "}\n",
            config);
    }

    [Fact]
    public void Compose_WithoutFallback_AnswersUnknownPathsWith404()
    {
        string config = NginxConfigFile.Compose(80, spaFallback: false, [], []);

        Assert.Contains("try_files $uri $uri/ =404;", config, StringComparison.Ordinal);
        Assert.DoesNotContain("/index.html;", config, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_WithoutStrip_ForwardsTheOriginalPath()
    {
        string config = NginxConfigFile.Compose(
            80,
            spaFallback: true,
            [new NginxProxyLocation("/api", new Uri("http://api-orders:8080"), StripPrefix: false, "API 'orders'")],
            []);

        Assert.Contains("# generated proxy route for API 'orders'\n", config, StringComparison.Ordinal);
        Assert.Contains("location = /api { return 301 /api/; }\n", config, StringComparison.Ordinal);
        Assert.Contains("location /api/ {\n", config, StringComparison.Ordinal);

        // No URI part on proxy_pass: nginx forwards /api/orders as /api/orders.
        Assert.Contains("proxy_pass http://api-orders:8080;", config, StringComparison.Ordinal);
        Assert.DoesNotContain("proxy_pass http://api-orders:8080/;", config, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_WithStrip_ReplacesTheMatchedPrefix()
    {
        string config = NginxConfigFile.Compose(
            80,
            spaFallback: true,
            [new NginxProxyLocation("/pricing/", new Uri("http://stub-pricing:80"), StripPrefix: true, "stub 'pricing'")],
            []);

        // The URI part '/' makes nginx replace /pricing/ with /, so /pricing/quotes becomes /quotes.
        // Port 80 is http's default, so the Uri normalizes it away; nginx reads both the same.
        Assert.Contains("proxy_pass http://stub-pricing/;", config, StringComparison.Ordinal);
        Assert.Contains("location = /pricing { return 301 /pricing/; }", config, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_AppendsExtraDirectivesVerbatim()
    {
        string config = NginxConfigFile.Compose(80, spaFallback: true, [], ["add_header X-Frame-Options DENY;"]);

        Assert.Contains("    # declared by the definition, verbatim\n    add_header X-Frame-Options DENY;\n", config, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeLocationPath_TrimsTheTrailingSlashAndKeepsTheRoot()
    {
        Assert.Equal("/api", NginxConfigFile.NormalizeLocationPath("/api/"));
        Assert.Equal("/api", NginxConfigFile.NormalizeLocationPath("/api"));
        Assert.Equal("/", NginxConfigFile.NormalizeLocationPath("/"));
    }

    [Fact]
    public void NormalizeLocationPath_RefusesAPathWithoutALeadingSlash()
    {
        Assert.Throws<FrameworkConfigurationException>(() => NginxConfigFile.NormalizeLocationPath("api"));
    }
}
