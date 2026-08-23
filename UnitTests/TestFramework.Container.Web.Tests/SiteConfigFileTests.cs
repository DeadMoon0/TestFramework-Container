using System;
using System.Collections.Generic;
using TestFramework.Container.Web.Sites;
using TestFramework.Core.Exceptions;
using Xunit;

namespace TestFramework.Container.Web.Tests;

/// <summary>
/// Covers the runtime configuration file: bound values merge over what the payload ships, so one
/// address is overridden without rebuilding the application's whole configuration shape.
/// </summary>
public class SiteConfigFileTests
{
    [Fact]
    public void Compose_NestsColonPathsIntoObjects()
    {
        string json = SiteConfigFile.Compose(null, new Dictionary<string, string> { ["backend:orders"] = "http://localhost:5080/" });

        Assert.Contains("\"backend\"", json, StringComparison.Ordinal);
        Assert.Contains("\"orders\": \"http://localhost:5080/\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_PreservesEveryFieldTheRunDoesNotBind()
    {
        string existing = "{ \"apiBaseUrl\": null, \"theme\": \"dark\", \"pageSize\": 25 }";

        string json = SiteConfigFile.Compose(existing, new Dictionary<string, string> { ["apiBaseUrl"] = "http://localhost:5080/" });

        Assert.Contains("\"apiBaseUrl\": \"http://localhost:5080/\"", json, StringComparison.Ordinal);
        Assert.Contains("\"theme\": \"dark\"", json, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\": 25", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_BoundPathsWinOverShippedValues()
    {
        string existing = "{ \"apiBaseUrl\": \"https://prod.example/\" }";

        string json = SiteConfigFile.Compose(existing, new Dictionary<string, string> { ["apiBaseUrl"] = "http://localhost:5080/" });

        Assert.DoesNotContain("prod.example", json, StringComparison.Ordinal);
        Assert.Contains("http://localhost:5080/", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_RefusesExistingContentThatIsNotJson()
    {
        FrameworkConfigurationException exception = Assert.Throws<FrameworkConfigurationException>(
            () => SiteConfigFile.Compose("window.__env = {};", new Dictionary<string, string> { ["a"] = "b" }));

        Assert.Contains("not valid JSON", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_RefusesExistingContentThatIsNotAnObject()
    {
        Assert.Throws<FrameworkConfigurationException>(
            () => SiteConfigFile.Compose("[1, 2]", new Dictionary<string, string> { ["a"] = "b" }));
    }

    [Fact]
    public void MergeJson_PatchesOneFieldOfTheExistingContent()
    {
        SiteConfigFileContext context = new(
            new SiteAddressBook(_ => new Uri("http://localhost:5080/"), _ => new Uri("http://localhost:5090/")),
            "{ \"apiBaseUrl\": null, \"theme\": \"dark\" }");

        string json = context.MergeJson(("apiBaseUrl", context.Addresses.ApiBaseUrl("orders").ToString()));

        Assert.Contains("\"apiBaseUrl\": \"http://localhost:5080/\"", json, StringComparison.Ordinal);
        Assert.Contains("\"theme\": \"dark\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void MergeJson_StartsFromAnEmptyObjectWhenThePayloadShipsNoFile()
    {
        SiteConfigFileContext context = new(
            new SiteAddressBook(_ => new Uri("http://localhost:5080/"), _ => new Uri("http://localhost:5090/")),
            existingContent: null);

        string json = context.MergeJson(("apiBaseUrl", "http://localhost:5080/"));

        Assert.Contains("\"apiBaseUrl\": \"http://localhost:5080/\"", json, StringComparison.Ordinal);
    }
}
