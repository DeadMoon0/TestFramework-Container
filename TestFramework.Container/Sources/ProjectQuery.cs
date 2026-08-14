using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Sources;

/// <summary>
/// What a project says about itself.
/// </summary>
/// <param name="ProjectPath">The project file that was asked.</param>
/// <param name="AssemblyName">The assembly the project produces.</param>
/// <param name="TargetFrameworks">Every framework the project targets, in declaration order.</param>
/// <param name="IsWebSdk">Whether the project uses the web SDK, which decides the runtime base image.</param>
/// <param name="ProjectReferences">Absolute paths of the projects it references.</param>
public sealed record ProjectFacts(
    string ProjectPath,
    string AssemblyName,
    IReadOnlyList<string> TargetFrameworks,
    bool IsWebSdk,
    IReadOnlyList<string> ProjectReferences)
{
    /// <summary>
    /// The directory holding the project file.
    /// </summary>
    public string ProjectDirectory => Path.GetDirectoryName(ProjectPath)!;

    /// <summary>
    /// The runtime image repository matching the project kind.
    /// </summary>
    public string RuntimeImageRepository => IsWebSdk
        ? "mcr.microsoft.com/dotnet/aspnet"
        : "mcr.microsoft.com/dotnet/runtime";
}

/// <summary>
/// Asks MSBuild what a project is, instead of inferring it from paths.
/// </summary>
/// <remarks>
/// MSBuild is the only thing that knows how a project is configured: a custom output path, the
/// artifacts layout, a multi-targeted framework list, the SDK in use. Reading those from the project
/// removes the guesses that a directory walk has to make, and it needs no change in the project
/// being asked.
/// </remarks>
public static class ProjectQuery
{
    /// <summary>
    /// Reads what a project declares about itself.
    /// </summary>
    /// <param name="projectPath">The absolute path of the project file.</param>
    /// <param name="cancellationToken">The cancellation token for the running query.</param>
    /// <exception cref="FrameworkConfigurationException">The project does not exist, or MSBuild could not evaluate it.</exception>
    public static async Task<ProjectFacts> ReadAsync(string projectPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        string fullPath = Path.GetFullPath(projectPath);
        if (!File.Exists(fullPath))
            throw new FrameworkConfigurationException($"The project '{fullPath}' does not exist.");

        DotNetCliResult result = await DotNetCli.RunAsync(
            [
                "msbuild",
                fullPath,
                "-getProperty:AssemblyName",
                "-getProperty:TargetFramework",
                "-getProperty:TargetFrameworks",
                "-getProperty:UsingMicrosoftNETSdkWeb",
                "-getItem:ProjectReference",
            ],
            Path.GetDirectoryName(fullPath),
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new FrameworkConfigurationException(
                $"MSBuild could not evaluate '{fullPath}'.",
                [
                    "Check that the project restores and builds on its own.",
                    "The full command output follows the message in the run log.",
                ],
                [result.Describe()]);
        }

        JsonObject payload = Parse(result, fullPath);
        JsonObject properties = payload["Properties"] as JsonObject ?? [];

        string assemblyName = ReadString(properties, "AssemblyName")
            ?? Path.GetFileNameWithoutExtension(fullPath);

        return new ProjectFacts(
            fullPath,
            assemblyName,
            ReadTargetFrameworks(properties),
            string.Equals(ReadString(properties, "UsingMicrosoftNETSdkWeb"), "true", StringComparison.OrdinalIgnoreCase),
            ReadProjectReferences(payload, Path.GetDirectoryName(fullPath)!));
    }

    /// <summary>
    /// Returns the directory that contains a project and everything it references.
    /// </summary>
    /// <param name="facts">The project facts.</param>
    /// <remarks>
    /// A build context has to include the referenced projects or the build fails inside the
    /// container, and it should include no more than that.
    /// </remarks>
    public static string ResolveCommonRoot(ProjectFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        string root = facts.ProjectDirectory;
        foreach (string reference in facts.ProjectReferences)
            root = CommonAncestor(root, Path.GetDirectoryName(reference)!);

        return root;
    }

    private static string CommonAncestor(string left, string right)
    {
        // Compared segment by segment rather than by string prefix, so 'App' and 'AppTests' are not
        // treated as one directory. Case handling follows the host: Windows paths are not case
        // sensitive, Linux paths are.
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string[] leftParts = Path.GetFullPath(left).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        string[] rightParts = Path.GetFullPath(right).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        int shared = 0;
        while (shared < leftParts.Length && shared < rightParts.Length && string.Equals(leftParts[shared], rightParts[shared], comparison))
            shared++;

        if (shared == 0)
            return Path.GetFullPath(left);

        string joined = string.Join(Path.DirectorySeparatorChar, leftParts.Take(shared));

        // A rooted Unix path loses its leading separator when split; a Windows drive keeps its own.
        return OperatingSystem.IsWindows() ? $"{joined}{Path.DirectorySeparatorChar}" : $"{Path.DirectorySeparatorChar}{joined}";
    }

    private static JsonObject Parse(DotNetCliResult result, string projectPath)
    {
        try
        {
            return JsonNode.Parse(result.StandardOutput) as JsonObject
                ?? throw new FrameworkConfigurationException($"MSBuild returned no usable evaluation for '{projectPath}'.");
        }
        catch (JsonException exception)
        {
            throw new FrameworkConfigurationException(
                $"MSBuild returned something other than JSON while evaluating '{projectPath}'.",
                ["Check that the installed SDK supports '-getProperty', which needs .NET 8 or newer."],
                [result.Describe()],
                exception);
        }
    }

    private static IReadOnlyList<string> ReadTargetFrameworks(JsonObject properties)
    {
        string? plural = ReadString(properties, "TargetFrameworks");
        if (!string.IsNullOrWhiteSpace(plural))
            return [.. plural.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

        string? single = ReadString(properties, "TargetFramework");
        return string.IsNullOrWhiteSpace(single) ? [] : [single];
    }

    private static IReadOnlyList<string> ReadProjectReferences(JsonObject payload, string projectDirectory)
    {
        if (payload["Items"] is not JsonObject items || items["ProjectReference"] is not JsonArray references)
            return [];

        List<string> resolved = [];
        foreach (JsonNode? reference in references)
        {
            string? relative = reference is JsonObject entry
                ? ReadString(entry, "FullPath") ?? ReadString(entry, "Identity")
                : reference?.ToString();

            if (!string.IsNullOrWhiteSpace(relative))
                resolved.Add(Path.GetFullPath(relative, projectDirectory));
        }

        return resolved;
    }

    private static string? ReadString(JsonObject source, string name)
        => source[name] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
}
