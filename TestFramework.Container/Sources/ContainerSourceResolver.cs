using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Sources;

/// <summary>
/// Turns a declared source into the plan that will be carried out.
/// </summary>
/// <remarks>
/// Planning has no side effects: nothing is built, no container is started, no image is pulled. That
/// is what lets an environment answer "what would this run" before anyone waits for Docker.
/// </remarks>
public static class ContainerSourceResolver
{
    /// <summary>
    /// Works out what a source will do.
    /// </summary>
    /// <param name="source">The declared source.</param>
    /// <param name="cancellationToken">The cancellation token for the running query.</param>
    /// <exception cref="FrameworkConfigurationException">The source cannot be carried out as declared.</exception>
    public static async Task<ContainerSourcePlan> PlanAsync(ContainerSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        return source switch
        {
            ImageContainerSource image => new ContainerSourcePlan { Kind = ContainerSourceKind.Image, Image = image.ImageReference },
            DirectoryContainerSource directory => PlanDirectory(directory),
            EntryPointContainerSource entryPoint => PlanEntryPoint(entryPoint),
            ProjectContainerSource project => await PlanProjectAsync(project, cancellationToken).ConfigureAwait(false),
            _ => throw new FrameworkConfigurationException($"The container source '{source.GetType().Name}' is not supported."),
        };
    }

    private static ContainerSourcePlan PlanDirectory(DirectoryContainerSource source)
    {
        if (!Directory.Exists(source.OutputDirectory))
            throw new FrameworkConfigurationException($"The directory '{source.OutputDirectory}' does not exist, so there is nothing to ship.");

        string? assembly = FindEntryAssembly(source.OutputDirectory);

        return new ContainerSourcePlan
        {
            Kind = ContainerSourceKind.Directory,
            OutputDirectory = source.OutputDirectory,
            AssemblyFileName = assembly,
            BuiltAtUtc = ReadTimestamp(source.OutputDirectory, assembly),
            Derivations = assembly is null
                ? ["no runtime configuration was found, so the entry assembly is unknown"]
                : [$"entry assembly from the runtime configuration beside it"],
        };
    }

    private static ContainerSourcePlan PlanEntryPoint(EntryPointContainerSource source)
    {
        ContainerOutput output = ContainerOutputResolver.ResolveProjectOutput(source.EntryPointType);

        return new ContainerSourcePlan
        {
            Kind = ContainerSourceKind.EntryPoint,
            TargetFramework = output.TargetFramework,
            OutputDirectory = output.OutputDirectory,
            AssemblyFileName = output.AssemblyFileName,
            BuiltAtUtc = output.AssemblyLastWriteTimeUtc,
            Derivations =
            [
                $"project from the loaded assembly of '{source.EntryPointType.Name}'",
                $"output from the owning project at '{output.ProjectDirectory}'",
                output.UsedFallbackOutput ? output.FallbackReason ?? "a fallback output was used" : "framework from the assembly's target framework attribute",
            ],
        };
    }

    private static async Task<ContainerSourcePlan> PlanProjectAsync(ProjectContainerSource source, CancellationToken cancellationToken)
    {
        ProjectFacts facts = await ProjectQuery.ReadAsync(source.ProjectPath, cancellationToken).ConfigureAwait(false);
        List<string> derivations = [];

        string targetFramework = ResolveTargetFramework(source, facts, derivations);
        string runtimeImage = await ResolveRuntimeImageAsync(source, facts, targetFramework, derivations, cancellationToken).ConfigureAwait(false);

        ContainerSourcePlan plan = new()
        {
            Kind = ContainerSourceKind.Project,
            Strategy = source.Strategy,
            ProjectPath = facts.ProjectPath,
            Configuration = source.Configuration,
            TargetFramework = targetFramework,
            RuntimeImage = runtimeImage,
            AssemblyFileName = $"{facts.AssemblyName}.dll",
            Derivations = derivations,
        };

        if (source.Strategy != ContainerBuildStrategy.InContainer)
            return plan;

        string context = source.ContextDirectory ?? ProjectQuery.ResolveCommonRoot(facts);
        if (source.ContextDirectory is null)
        {
            derivations.Add(facts.ProjectReferences.Count == 0
                ? "context from the project directory, which references no other project"
                : $"context from the closest directory containing the project and its {facts.ProjectReferences.Count} referenced project(s)");
        }

        return plan with
        {
            ContextDirectory = context,
            SdkImage = await ResolveSdkImageAsync(source, targetFramework, derivations, cancellationToken).ConfigureAwait(false),
            Derivations = derivations,
        };
    }

    private static string ResolveTargetFramework(ProjectContainerSource source, ProjectFacts facts, List<string> derivations)
    {
        if (source.TargetFramework is { } declared)
        {
            if (facts.TargetFrameworks.Count > 0 && !facts.TargetFrameworks.Contains(declared, StringComparer.OrdinalIgnoreCase))
            {
                throw new FrameworkConfigurationException(
                    $"'{facts.ProjectPath}' does not target '{declared}'.",
                    [$"Choose one of: {string.Join(", ", facts.TargetFrameworks)}."]);
            }

            return declared;
        }

        if (facts.TargetFrameworks.Count == 0)
            throw new FrameworkConfigurationException($"'{facts.ProjectPath}' declares no target framework, so no runtime image can be chosen.");

        if (facts.TargetFrameworks.Count > 1)
        {
            // Picking silently would mean a project that adds a framework quietly changes what the
            // test runs.
            throw new FrameworkConfigurationException(
                $"'{facts.ProjectPath}' targets {string.Join(", ", facts.TargetFrameworks)}, so which one to run is ambiguous.",
                [$"Call WithTargetFramework(\"{facts.TargetFrameworks[0]}\") on the source."]);
        }

        derivations.Add($"framework from the project's single target, '{facts.TargetFrameworks[0]}'");
        return facts.TargetFrameworks[0];
    }

    private static async Task<string> ResolveRuntimeImageAsync(
        ProjectContainerSource source,
        ProjectFacts facts,
        string targetFramework,
        List<string> derivations,
        CancellationToken cancellationToken)
    {
        if (source.RuntimeImage is { } declared)
            return declared;

        string version = ToImageVersion(targetFramework, facts.ProjectPath);
        string repository = facts.RuntimeImageRepository;
        derivations.Add(facts.IsWebSdk
            ? "runtime image from the project using the web SDK"
            : "runtime image from the project not using the web SDK");

        await Task.CompletedTask.ConfigureAwait(false);
        return $"{repository}:{version}";
    }

    private static async Task<string> ResolveSdkImageAsync(
        ProjectContainerSource source,
        string targetFramework,
        List<string> derivations,
        CancellationToken cancellationToken)
    {
        if (source.SdkImage is { } declared)
            return declared;

        // The SDK on the path already builds this project, so it is the safest tag to build it with
        // again; the project's own framework can be older than the SDK that compiles it.
        string? hostSdk = await DotNetCli.ReadSdkMajorMinorAsync(cancellationToken).ConfigureAwait(false);
        if (hostSdk is not null)
        {
            derivations.Add($"SDK image from the SDK on this machine, {hostSdk}");
            return $"mcr.microsoft.com/dotnet/sdk:{hostSdk}";
        }

        string version = ToImageVersion(targetFramework, source.ProjectPath);
        derivations.Add("SDK image from the target framework, because the installed SDK could not be read");
        return $"mcr.microsoft.com/dotnet/sdk:{version}";
    }

    private static string ToImageVersion(string targetFramework, string projectPath)
    {
        if (!targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase)
            || !targetFramework[3..].Contains('.', StringComparison.Ordinal))
        {
            throw new FrameworkConfigurationException(
                $"No image tag can be derived from the target framework '{targetFramework}' of '{projectPath}'.",
                ["Name the image explicitly with WithRuntimeImage(\"...\")."]);
        }

        return targetFramework[3..];
    }

    private static string? FindEntryAssembly(string outputDirectory)
    {
        // A runtime configuration sits beside the assembly it belongs to, which identifies the entry
        // point without having to guess from file names.
        string? runtimeConfig = Directory
            .EnumerateFiles(outputDirectory, "*.runtimeconfig.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault();

        if (runtimeConfig is null)
            return null;

        string name = Path.GetFileName(runtimeConfig)[..^".runtimeconfig.json".Length];
        return File.Exists(Path.Combine(outputDirectory, $"{name}.dll")) ? $"{name}.dll" : null;
    }

    private static DateTimeOffset? ReadTimestamp(string outputDirectory, string? assemblyFileName)
    {
        if (assemblyFileName is null)
            return null;

        string path = Path.Combine(outputDirectory, assemblyFileName);
        return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;
    }
}
