using System;
using System.Collections.Generic;
using System.Linq;

namespace TestFramework.Container.Web.Sites;

/// <summary>
/// What will be done to get a site's payload into its container, stated before it happens.
/// </summary>
/// <remarks>
/// Every value in the plan was either declared on the source or read from the project by a stated
/// rule. The environment logs the plan before acting on it, so a run's output says what was shipped
/// rather than leaving it to be inferred from a container that may already be gone.
/// </remarks>
public sealed record SiteSourcePlan
{
    /// <summary>
    /// What kind of source the payload comes from.
    /// </summary>
    public required SiteSourceKind Kind { get; init; }

    /// <summary>
    /// The image that runs as-is, for an image source.
    /// </summary>
    public string? Image { get; init; }

    /// <summary>
    /// The directory holding the sources, for a built payload.
    /// </summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>
    /// The image the build runs in, when it runs inside a container.
    /// </summary>
    public string? BuildImage { get; init; }

    /// <summary>
    /// The build that will run, described as its command line.
    /// </summary>
    public string? BuildCommand { get; init; }

    /// <summary>
    /// The directory whose content is copied into the container.
    /// </summary>
    public string? DistDirectory { get; init; }

    /// <summary>
    /// When the payload's index was last written, for a payload that already existed.
    /// </summary>
    public DateTimeOffset? BuiltAtUtc { get; init; }

    /// <summary>
    /// The scratch directory a container build copied the sources into, holding the build output the
    /// container ships. The run's litter: removed when the site is deconstructed.
    /// </summary>
    public string? TemporaryRoot { get; init; }

    /// <summary>
    /// The rules that filled in what was not declared.
    /// </summary>
    public IReadOnlyList<string> Derivations { get; init; } = [];

    /// <summary>
    /// Returns the plan as the log lines the environment writes before acting on it.
    /// </summary>
    /// <param name="identifier">The site identifier the plan belongs to.</param>
    public IReadOnlyList<string> ToLogLines(string identifier)
    {
        List<string> lines = [$"Site '{identifier}' plan:", $"  kind  {Describe()}"];

        if (ProjectDirectory is not null)
            lines.Add($"  project  {ProjectDirectory}");

        if (BuildCommand is not null)
            lines.Add($"  build  {BuildCommand}{(BuildImage is null ? string.Empty : $" (in {BuildImage})")}");

        if (DistDirectory is not null)
            lines.Add($"  dist  {DistDirectory}");

        if (BuiltAtUtc is { } builtAt)
            lines.Add($"  built  {builtAt:u}");

        lines.AddRange(Derivations.Select(derivation => $"  derived  {derivation}"));
        return lines;
    }

    /// <summary>
    /// Returns a readable description of the plan.
    /// </summary>
    public override string ToString() => Describe();

    private string Describe() => Kind switch
    {
        SiteSourceKind.Image => $"image {Image}",
        SiteSourceKind.Directory => $"directory {DistDirectory}",
        SiteSourceKind.NpmProject => $"npm project {ProjectDirectory}",
        SiteSourceKind.ContainerBuild => $"container build of {ProjectDirectory}",
        _ => Kind.ToString(),
    };
}
