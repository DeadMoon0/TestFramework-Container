using System;
using System.Collections.Generic;
using System.Linq;
using DotNet.Testcontainers.Containers;
using TestFramework.Container.Web.Sites;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Web;

/// <summary>
/// One running site container.
/// </summary>
/// <remarks>
/// The plan and the generated content are kept so a test can state what actually ran, rather than
/// having to infer it from a container that may already be gone.
/// </remarks>
public sealed record RunningSite
{
    internal RunningSite(string identifier, IContainer container, Uri baseUrl, SiteSourcePlan plan, string? nginxConfig, IReadOnlyDictionary<string, string> generatedFiles)
    {
        this.Identifier = identifier;
        this.Container = container;
        this.BaseUrl = baseUrl;
        this.Plan = plan;
        this.NginxConfig = nginxConfig;
        this.GeneratedFiles = generatedFiles;
    }

    /// <summary>
    /// The site identifier it serves.
    /// </summary>
    public string Identifier { get; }

    /// <summary>
    /// The running container.
    /// </summary>
    public IContainer Container { get; }

    /// <summary>
    /// The address a browser on the test host reaches it at.
    /// </summary>
    public Uri BaseUrl { get; }

    /// <summary>
    /// What was done to get the payload into the container.
    /// </summary>
    public SiteSourcePlan Plan { get; }

    /// <summary>
    /// The exact server configuration generated, or <see langword="null"/> for an image source.
    /// </summary>
    public string? NginxConfig { get; }

    /// <summary>
    /// Every configuration file written into the payload, by payload-relative path.
    /// </summary>
    public IReadOnlyDictionary<string, string> GeneratedFiles { get; }
}

/// <summary>
/// The site containers a run started.
/// </summary>
public sealed class SiteComponentState
{
    internal SiteComponentState(IReadOnlyList<RunningSite> sites)
    {
        Sites = sites;
    }

    /// <summary>
    /// The running sites, in the order they were started.
    /// </summary>
    public IReadOnlyList<RunningSite> Sites { get; }

    /// <summary>
    /// Returns a running site by identifier.
    /// </summary>
    /// <param name="identifier">The site identifier.</param>
    /// <exception cref="FrameworkStateException">No site was started for the identifier.</exception>
    public RunningSite GetRequiredSite(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);

        RunningSite? site = Sites.FirstOrDefault(candidate => string.Equals(candidate.Identifier, identifier, StringComparison.Ordinal));
        if (site is not null)
            return site;

        throw new FrameworkStateException($"No site was started for the identifier '{identifier}'. Started: {(Sites.Count == 0 ? "none" : string.Join(", ", Sites.Select(candidate => candidate.Identifier)))}.");
    }
}
