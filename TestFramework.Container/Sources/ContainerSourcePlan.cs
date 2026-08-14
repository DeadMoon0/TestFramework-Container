using System;
using System.Collections.Generic;
using System.Globalization;

namespace TestFramework.Container.Sources;

/// <summary>
/// What will happen to get an application into its container, stated before anything happens.
/// </summary>
/// <remarks>
/// Every value here is either declared by the caller or read from the project, and the ones that
/// were derived say so in <see cref="Derivations"/>. When a container fails to start, the question
/// is almost always "what did it actually try to run" -- this is the answer, and it is available
/// without starting anything.
/// </remarks>
public sealed record ContainerSourcePlan
{
    /// <summary>
    /// What kind of source this plan came from.
    /// </summary>
    public required ContainerSourceKind Kind { get; init; }

    /// <summary>
    /// Where the project is built, when the plan builds one.
    /// </summary>
    public ContainerBuildStrategy? Strategy { get; init; }

    /// <summary>
    /// The project being built.
    /// </summary>
    public string? ProjectPath { get; init; }

    /// <summary>
    /// The build configuration.
    /// </summary>
    public string? Configuration { get; init; }

    /// <summary>
    /// The framework being built or shipped.
    /// </summary>
    public string? TargetFramework { get; init; }

    /// <summary>
    /// The build context, when the build needs one.
    /// </summary>
    public string? ContextDirectory { get; init; }

    /// <summary>
    /// The SDK image, when the build runs in a container.
    /// </summary>
    public string? SdkImage { get; init; }

    /// <summary>
    /// The runtime base image the application ends up on.
    /// </summary>
    public string? RuntimeImage { get; init; }

    /// <summary>
    /// The image that will be run, once it exists.
    /// </summary>
    public string? Image { get; init; }

    /// <summary>
    /// The directory that will be shipped, for a plan that ships files rather than an image.
    /// </summary>
    public string? OutputDirectory { get; init; }

    /// <summary>
    /// The entry assembly file name, for a plan that ships files.
    /// </summary>
    public string? AssemblyFileName { get; init; }

    /// <summary>
    /// When the shipped assembly was last written, for a plan that ships files it did not build.
    /// </summary>
    public DateTimeOffset? BuiltAtUtc { get; init; }

    /// <summary>
    /// Values that were worked out rather than declared, and where each came from.
    /// </summary>
    public IReadOnlyList<string> Derivations { get; init; } = [];

    /// <summary>
    /// Renders the plan as a block of log lines.
    /// </summary>
    /// <param name="identifier">The identifier the plan belongs to.</param>
    public IReadOnlyList<string> ToLogLines(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);

        List<string> lines = [$"'{identifier}' source plan"];

        Add(lines, "kind", Strategy is { } strategy ? $"{Kind} ({Describe(strategy)})" : Kind.ToString());
        Add(lines, "project", ProjectPath);
        Add(lines, "configuration", Configuration);
        Add(lines, "framework", TargetFramework);
        Add(lines, "context", ContextDirectory);
        Add(lines, "sdk image", SdkImage);
        Add(lines, "runtime", RuntimeImage);
        Add(lines, "image", Image);
        Add(lines, "output", OutputDirectory);
        Add(lines, "entry", AssemblyFileName);
        Add(lines, "built", BuiltAtUtc?.ToString("u", CultureInfo.InvariantCulture));

        foreach (string derivation in Derivations)
            lines.Add($"  derived         {derivation}");

        return lines;
    }

    /// <summary>
    /// Returns a one-line summary of the plan.
    /// </summary>
    public override string ToString() => Kind switch
    {
        ContainerSourceKind.Image => $"image {Image}",
        ContainerSourceKind.Project => $"project {ProjectPath} ({Strategy})",
        ContainerSourceKind.Directory => $"directory {OutputDirectory}",
        _ => $"entry point output {OutputDirectory}",
    };

    private static string Describe(ContainerBuildStrategy strategy) => strategy switch
    {
        ContainerBuildStrategy.SdkContainerPublish => "image built by the SDK",
        ContainerBuildStrategy.HostPublish => "published on the host",
        _ => "built in a container",
    };

    private static void Add(List<string> lines, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            lines.Add($"  {label,-15} {value}");
    }
}
