using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Web.Sites;

/// <summary>
/// Turns a declared site source into the plan the environment acts on.
/// </summary>
/// <remarks>
/// Planning has no side effects: nothing is built, copied or written here. What planning cannot
/// answer it reports as an error naming the declaration that would answer it.
/// </remarks>
public static class SiteSourceResolver
{
    /// <summary>
    /// The dist locations probed, in order, when an npm project declares none. The Angular layout
    /// comes first because it nests one level deeper than the others.
    /// </summary>
    private static readonly string[] DistProbePatterns =
    [
        "dist/*/browser",
        "dist/*",
        "dist",
        "build",
    ];

    /// <summary>
    /// Plans how a source's payload is obtained.
    /// </summary>
    /// <param name="source">The declared source.</param>
    /// <exception cref="FrameworkConfigurationException">The declaration cannot be planned.</exception>
    public static SiteSourcePlan Plan(SiteSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return source switch
        {
            ImageSiteSource image => new SiteSourcePlan { Kind = SiteSourceKind.Image, Image = image.ImageReference },
            DirectorySiteSource directory => PlanDirectory(directory),
            NpmProjectSiteSource npm => PlanNpmProject(npm),
            ContainerBuildSiteSource build => PlanContainerBuild(build),
            _ => throw new FrameworkConfigurationException($"The site source '{source.GetType().Name}' is not supported."),
        };
    }

    private static SiteSourcePlan PlanDirectory(DirectorySiteSource source)
    {
        EnsurePayloadDirectory(source.DistDirectory, "Declare the directory that holds the built site, with its index.html at the root.");

        return new SiteSourcePlan
        {
            Kind = SiteSourceKind.Directory,
            DistDirectory = source.DistDirectory,
            BuiltAtUtc = File.GetLastWriteTimeUtc(Path.Combine(source.DistDirectory, "index.html")),
            Derivations = ["payload from an already-built directory; nothing checks it is current"],
        };
    }

    private static SiteSourcePlan PlanNpmProject(NpmProjectSiteSource source)
    {
        if (!File.Exists(Path.Combine(source.ProjectDirectory, "package.json")))
        {
            throw new FrameworkConfigurationException(
                $"The npm project directory '{source.ProjectDirectory}' has no package.json.",
                ["Point SiteSource.NpmProject(...) at the directory that holds package.json."]);
        }

        List<string> derivations = [];
        string distDirectory;
        if (source.DistPath is { } declared)
        {
            distDirectory = Path.GetFullPath(declared, source.ProjectDirectory);
        }
        else
        {
            distDirectory = ProbeDist(source.ProjectDirectory, out string derivation);
            derivations.Add(derivation);
        }

        return new SiteSourcePlan
        {
            Kind = SiteSourceKind.NpmProject,
            ProjectDirectory = source.ProjectDirectory,
            BuildImage = source.Strategy == SiteBuildStrategy.InContainerBuild ? source.BuildImage : null,
            BuildCommand = $"npm run {source.BuildScript}",
            DistDirectory = distDirectory,
            Derivations = derivations,
        };
    }

    private static SiteSourcePlan PlanContainerBuild(ContainerBuildSiteSource source)
    {
        if (!Directory.Exists(source.ProjectDirectory))
        {
            throw new FrameworkConfigurationException(
                $"The project directory '{source.ProjectDirectory}' does not exist.",
                ["Point SiteSource.ContainerBuild(...) at the directory that holds the sources."]);
        }

        if (source.DistPath is not { } distPath)
        {
            throw new FrameworkConfigurationException(
                $"The container build of '{source.ProjectDirectory}' declares no output directory, and an arbitrary toolchain has no conventions to probe.",
                ["Call WithDistPath(\"...\") with the output directory the build writes, relative to the project."]);
        }

        return new SiteSourcePlan
        {
            Kind = SiteSourceKind.ContainerBuild,
            ProjectDirectory = source.ProjectDirectory,
            BuildImage = source.BuildImage,
            BuildCommand = string.Join(' ', source.BuildCommand),
            DistDirectory = Path.GetFullPath(distPath, source.ProjectDirectory),
        };
    }

    private static string ProbeDist(string projectDirectory, out string derivation)
    {
        // The probe accepts exactly one answer. Two plausible dist folders mean the convention has
        // stopped being a convention, and guessing between them ships the wrong site silently.
        List<string> candidates = [];
        foreach (string pattern in DistProbePatterns)
        {
            candidates.AddRange(ResolvePattern(projectDirectory, pattern)
                .Where(candidate => File.Exists(Path.Combine(candidate, "index.html"))));

            if (candidates.Count > 0)
                break;
        }

        if (candidates.Count == 1)
        {
            derivation = $"dist probed by convention: {candidates[0]}";
            return candidates[0];
        }

        if (candidates.Count > 1)
        {
            throw new FrameworkConfigurationException(
                $"The npm project '{projectDirectory}' has more than one plausible output directory: {string.Join(", ", candidates.Select(candidate => $"'{candidate}'"))}.",
                ["Call WithDistPath(\"...\") to declare which one is the site."]);
        }

        throw new FrameworkConfigurationException(
            $"No output directory with an index.html was found under '{projectDirectory}' (probed: {string.Join(", ", DistProbePatterns)}).",
            [
                "Call WithDistPath(\"...\") to declare the output directory the build writes.",
                "The directory may simply not exist yet; declaring it lets the build create it.",
            ]);
    }

    private static IEnumerable<string> ResolvePattern(string projectDirectory, string pattern)
    {
        string[] segments = pattern.Split('/');
        IEnumerable<string> current = [projectDirectory];

        foreach (string segment in segments)
        {
            current = segment == "*"
                ? current.SelectMany(directory => Directory.Exists(directory) ? Directory.EnumerateDirectories(directory) : [])
                : current.Select(directory => Path.Combine(directory, segment));
        }

        return current.Where(Directory.Exists).Select(Path.GetFullPath).OrderBy(path => path, StringComparer.Ordinal);
    }

    internal static void EnsurePayloadDirectory(string distDirectory, string recovery)
    {
        if (!Directory.Exists(distDirectory))
            throw new FrameworkConfigurationException($"The site directory '{distDirectory}' does not exist.", [recovery]);

        if (!File.Exists(Path.Combine(distDirectory, "index.html")))
            throw new FrameworkConfigurationException($"The site directory '{distDirectory}' has no index.html at its root, so a web server would answer with errors.", [recovery]);
    }
}
