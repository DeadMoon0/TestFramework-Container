using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container;

/// <summary>
/// The build output of a project that is mounted into a container.
/// </summary>
/// <param name="ProjectDirectory">The directory holding the project file.</param>
/// <param name="OutputDirectory">The directory holding the built assemblies.</param>
/// <param name="TargetFramework">The target framework moniker, for example <c>net8.0</c>.</param>
/// <param name="AssemblyName">The assembly name without extension.</param>
public sealed record ContainerOutput(string ProjectDirectory, string OutputDirectory, string TargetFramework, string AssemblyName)
{
    /// <summary>
    /// The directory the assembly was actually loaded from, before any fallback.
    /// </summary>
    public string? InitialOutputDirectory { get; init; }

    /// <summary>
    /// Whether the preferred location was unusable, so the second candidate was chosen.
    /// </summary>
    /// <remarks>
    /// Which location is preferred depends on the method used. <see cref="FallbackReason"/> says
    /// what happened.
    /// </remarks>
    public bool UsedFallbackOutput { get; init; }

    /// <summary>
    /// Why the fallback was used, when it was.
    /// </summary>
    public string? FallbackReason { get; init; }

    /// <summary>
    /// The entry assembly file name, as passed to <c>dotnet</c> inside the container.
    /// </summary>
    public string AssemblyFileName => $"{AssemblyName}.dll";

    /// <summary>
    /// When the shipped assembly was last written.
    /// </summary>
    /// <remarks>
    /// Nothing here builds anything: the output is expected to exist already, normally because a
    /// project reference made the test build produce it. That leaves one failure this cannot
    /// prevent -- shipping a stale build and testing last week's code -- so the timestamp is
    /// reported instead of assumed.
    /// </remarks>
    public DateTimeOffset? AssemblyLastWriteTimeUtc => ReadTimestamp();

    private DateTimeOffset? ReadTimestamp()
    {
        string path = Path.Combine(OutputDirectory, AssemblyFileName);
        return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;
    }
}

/// <summary>
/// Locates the build output of an application so it can be mounted into a runtime image.
/// </summary>
/// <remarks>
/// Mounting build output avoids a publish step on every run. The resolver refuses to guess: when the
/// output does not contain the files the caller requires, it fails with every path it examined rather
/// than starting a container that will fail obscurely.
/// </remarks>
public static class ContainerOutputResolver
{
    /// <summary>
    /// Resolves the build output for the assembly containing a type.
    /// </summary>
    /// <param name="entryPointType">A type from the application assembly.</param>
    /// <param name="requiredFiles">
    /// File names that must exist in the output, relative to it. The assembly itself is always
    /// required; pass extra markers such as <c>host.json</c> when the runtime needs them.
    /// </param>
    /// <exception cref="FrameworkConfigurationException">The output could not be located.</exception>
    public static ContainerOutput Resolve(Type entryPointType, params string[] requiredFiles)
    {
        ArgumentNullException.ThrowIfNull(entryPointType);

        string assemblyLocation = entryPointType.Assembly.Location;
        if (string.IsNullOrWhiteSpace(assemblyLocation))
            throw new FrameworkConfigurationException($"Could not resolve an assembly location for '{entryPointType.FullName}'. Single-file or in-memory assemblies cannot be mounted into a container.");

        string loadedOutputDirectory = Path.GetDirectoryName(assemblyLocation)
            ?? throw new DirectoryNotFoundException($"Could not locate the output directory for '{entryPointType.FullName}'.");

        return ResolveFrom(entryPointType, loadedOutputDirectory, requiredFiles);
    }

    /// <summary>
    /// Resolves the owning project's own build output, preferring it over the loaded location.
    /// </summary>
    /// <param name="entryPointType">A type from the application assembly.</param>
    /// <param name="requiredFiles">
    /// File names that must exist in the output, relative to it. The assembly itself is always
    /// required.
    /// </param>
    /// <remarks>
    /// Use this when shipping an application into a container. A project-referenced assembly is
    /// copied into the referencing project's output as well, so the location it was loaded from is
    /// often a test project's directory: complete enough to run, but full of assemblies the
    /// application does not need, and misleading about what was shipped.
    /// </remarks>
    /// <exception cref="FrameworkConfigurationException">The output could not be located.</exception>
    public static ContainerOutput ResolveProjectOutput(Type entryPointType, params string[] requiredFiles)
    {
        ArgumentNullException.ThrowIfNull(entryPointType);
        ArgumentNullException.ThrowIfNull(requiredFiles);

        Assembly assembly = entryPointType.Assembly;
        if (string.IsNullOrWhiteSpace(assembly.Location))
            throw new FrameworkConfigurationException($"Could not resolve an assembly location for '{entryPointType.FullName}'. Single-file or in-memory assemblies cannot be shipped into a container.");

        string loadedOutputDirectory = Path.GetDirectoryName(assembly.Location)
            ?? throw new DirectoryNotFoundException($"Could not locate the output directory for '{entryPointType.FullName}'.");

        string assemblyName = assembly.GetName().Name
            ?? throw new FrameworkStateException($"The assembly name for '{entryPointType.FullName}' could not be resolved.");

        string targetFramework = ResolveTargetFramework(assembly);
        string projectDirectory = ResolveProjectDirectory(assemblyName, loadedOutputDirectory);
        string[] required = [$"{assemblyName}.dll", .. requiredFiles];
        string projectOutput = Path.Combine(projectDirectory, "bin", ResolveBuildConfiguration(loadedOutputDirectory), targetFramework);

        if (LooksComplete(projectOutput, required))
            return new ContainerOutput(projectDirectory, projectOutput, targetFramework, assemblyName) { InitialOutputDirectory = loadedOutputDirectory };

        if (LooksComplete(loadedOutputDirectory, required))
        {
            return new ContainerOutput(projectDirectory, loadedOutputDirectory, targetFramework, assemblyName)
            {
                InitialOutputDirectory = loadedOutputDirectory,
                UsedFallbackOutput = true,
                FallbackReason = $"The project's own output ('{projectOutput}') does not contain {string.Join(" and ", required)}, so the location the assembly was loaded from is shipped instead.",
            };
        }

        throw CreateMissingOutputException(entryPointType, projectDirectory, projectOutput, loadedOutputDirectory, required);
    }

    /// <summary>
    /// Resolves the build output starting from an explicit directory.
    /// </summary>
    /// <param name="entryPointType">A type from the application assembly.</param>
    /// <param name="loadedOutputDirectory">The directory the assembly was loaded from.</param>
    /// <param name="requiredFiles">File names that must exist in the output, besides the assembly itself.</param>
    /// <exception cref="FrameworkConfigurationException">The output could not be located.</exception>
    public static ContainerOutput ResolveFrom(Type entryPointType, string loadedOutputDirectory, IReadOnlyList<string> requiredFiles)
    {
        ArgumentNullException.ThrowIfNull(entryPointType);
        ArgumentException.ThrowIfNullOrWhiteSpace(loadedOutputDirectory);
        ArgumentNullException.ThrowIfNull(requiredFiles);

        Assembly assembly = entryPointType.Assembly;
        string assemblyName = assembly.GetName().Name
            ?? throw new FrameworkStateException($"The assembly name for '{entryPointType.FullName}' could not be resolved.");

        string targetFramework = ResolveTargetFramework(assembly);
        string projectDirectory = ResolveProjectDirectory(assemblyName, loadedOutputDirectory);
        string[] required = [$"{assemblyName}.dll", .. requiredFiles];

        if (LooksComplete(loadedOutputDirectory, required))
            return new ContainerOutput(projectDirectory, loadedOutputDirectory, targetFramework, assemblyName) { InitialOutputDirectory = loadedOutputDirectory };

        // A shadow-copying or multi-targeting test host can leave the assembly somewhere other than
        // the project's own output, so try the conventional location before giving up.
        string fallback = Path.Combine(projectDirectory, "bin", ResolveBuildConfiguration(loadedOutputDirectory), targetFramework);
        if (LooksComplete(fallback, required))
        {
            return new ContainerOutput(projectDirectory, fallback, targetFramework, assemblyName)
            {
                InitialOutputDirectory = loadedOutputDirectory,
                UsedFallbackOutput = true,
                FallbackReason = $"The assembly was loaded from a copied build location ('{loadedOutputDirectory}') that does not contain {string.Join(" and ", required)}.",
            };
        }

        throw CreateMissingOutputException(entryPointType, projectDirectory, loadedOutputDirectory, fallback, required);
    }

    /// <summary>
    /// Returns the target framework moniker an assembly was built for.
    /// </summary>
    /// <param name="assembly">The assembly to inspect.</param>
    public static string ResolveTargetFramework(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        string? frameworkName = assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
        if (string.IsNullOrWhiteSpace(frameworkName))
            throw new FrameworkConfigurationException($"Assembly '{assembly.GetName().Name}' does not declare a target framework, so no matching runtime image can be chosen.");

        // ".NETCoreApp,Version=v10.0" -> "net10.0"
        string[] parts = frameworkName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string version = parts.FirstOrDefault(part => part.StartsWith("Version=v", StringComparison.OrdinalIgnoreCase))?["Version=v".Length..]
            ?? throw new FrameworkConfigurationException($"The target framework '{frameworkName}' could not be parsed into a moniker.");

        return $"net{version}";
    }

    private static bool LooksComplete(string directory, IReadOnlyList<string> requiredFiles)
        => Directory.Exists(directory) && requiredFiles.All(file => File.Exists(Path.Combine(directory, file)));

    /// <summary>
    /// How deep below a candidate directory a project file is still looked for.
    /// </summary>
    /// <remarks>
    /// A project's own <c>bin/Configuration/tfm[/runtime]</c> is four levels; a solution folder above it
    /// adds a couple more. Beyond that the answer would not be this project's file anyway, and the depth
    /// is what keeps the search from walking a whole drive.
    /// </remarks>
    private const int ProjectSearchDepth = 8;

    private static readonly EnumerationOptions ProjectSearchOptions = new()
    {
        RecurseSubdirectories = true,

        // The default overload maps to EnumerationOptions.Compatible, which throws on the first
        // directory the process may not read. One protected system directory anywhere below the search
        // root would end the run with an UnauthorizedAccessException nothing in this class catches.
        IgnoreInaccessible = true,

        // A junction or symlink pointing back up turns the walk into a loop.
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        MaxRecursionDepth = ProjectSearchDepth,
    };

    private static string ResolveBuildConfiguration(string outputDirectory)
    {
        // The segment right after 'bin' is the configuration, whatever it is called. A project built
        // as 'Staging' has no 'Release' anywhere in its path and is not a Debug build either.
        string[] segments = outputDirectory.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < segments.Length - 1; index++)
        {
            if (string.Equals(segments[index], "bin", StringComparison.OrdinalIgnoreCase))
                return segments[index + 1];
        }

        // An output that does not sit under 'bin' at all still usually says which configuration it is.
        if (outputDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            return "Release";

        return "Debug";
    }

    /// <summary>
    /// Walks up from the build output looking for the project file that produced it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The climb stops at the repository boundary. Above it the search would only find other people's
    /// projects, and without a stop it runs to the drive root — which is where the old version went
    /// whenever the assembly name differed from the project name, because "no match" and "too many
    /// matches" were both treated as "keep climbing".
    /// </para>
    /// <para>
    /// More than one match is now an error rather than a reason to climb: climbing widens the search, so
    /// a level that found two files will only ever find those two and more.
    /// </para>
    /// </remarks>
    private static string ResolveProjectDirectory(string assemblyName, string startDirectory)
    {
        List<string> searched = [];

        for (DirectoryInfo? current = new(startDirectory); current is not null; current = current.Parent)
        {
            searched.Add(current.FullName);

            string[] matches = FindProjectFiles(current.FullName, assemblyName);
            if (matches.Length == 1)
                return Path.GetDirectoryName(matches[0])!;

            if (matches.Length > 1)
            {
                throw new FrameworkConfigurationException(
                    $"More than one '{assemblyName}.csproj' exists below '{current.FullName}', so the project directory for assembly '{assemblyName}' is ambiguous: "
                    + string.Join(", ", matches.OrderBy(match => match, StringComparer.OrdinalIgnoreCase))
                    + ". Name the project explicitly instead of letting it be discovered.");
            }

            if (ContainerRepositoryBoundary.IsBoundary(current))
                break;
        }

        throw new FrameworkConfigurationException(
            $"Could not locate the project directory for assembly '{assemblyName}'. No '{assemblyName}.csproj' was found below any of: "
            + string.Join(", ", searched)
            + ". The search stops at the repository boundary, so a project outside the repository has to be named explicitly.");
    }

    private static string[] FindProjectFiles(string directory, string assemblyName)
    {
        try
        {
            return Directory.GetFiles(directory, $"{assemblyName}.csproj", ProjectSearchOptions);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            // A directory that cannot be read is not a reason to stop climbing.
            return [];
        }
    }


    private static FrameworkConfigurationException CreateMissingOutputException(
        Type entryPointType,
        string projectDirectory,
        string selectedOutputDirectory,
        string fallbackOutputDirectory,
        IReadOnlyList<string> requiredFiles)
    {
        List<string> details =
        [
            $"'{entryPointType.FullName}' must resolve to build output before its container can start.",
            $"Project directory: {projectDirectory}",
            $"Checked output directory: {selectedOutputDirectory}",
        ];

        if (!string.Equals(fallbackOutputDirectory, selectedOutputDirectory, StringComparison.OrdinalIgnoreCase))
            details.Add($"Checked fallback directory: {fallbackOutputDirectory}");

        details.Add($"Expected files: {string.Join(", ", requiredFiles)}");
        details.Add("Build or publish the project before starting the container environment.");

        return new FrameworkConfigurationException(string.Join(Environment.NewLine, details));
    }
}
