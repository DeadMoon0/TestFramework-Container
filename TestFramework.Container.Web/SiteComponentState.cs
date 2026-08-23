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
/// <param name="Identifier">The site identifier it serves.</param>
/// <param name="Container">The running container.</param>
/// <param name="BaseUrl">The address a browser on the test host reaches it at.</param>
/// <param name="Plan">What was done to get the payload into the container.</param>
/// <param name="NginxConfig">The exact server configuration generated, or <see langword="null"/> for an image source.</param>
/// <param name="GeneratedFiles">Every configuration file written into the payload, by payload-relative path.</param>
/// <remarks>
/// The plan and the generated content are kept so a test can state what actually ran, rather than
/// having to infer it from a container that may already be gone.
/// </remarks>
public sealed record RunningSite(
    string Identifier,
    IContainer Container,
    Uri BaseUrl,
    SiteSourcePlan Plan,
    string? NginxConfig,
    IReadOnlyDictionary<string, string> GeneratedFiles);

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
