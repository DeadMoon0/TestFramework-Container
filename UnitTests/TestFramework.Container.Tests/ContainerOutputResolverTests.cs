using System;
using System.IO;
using TestFramework.Core.Exceptions;
using Xunit;

namespace TestFramework.Container.Tests;

/// <summary>
/// Covers build-output resolution, which decides what gets mounted into a container.
/// </summary>
/// <remarks>
/// These moved here from the Azure test suite when the resolver became shared. They no longer reach
/// into private methods by reflection, because the resolver is public API of this package.
/// </remarks>
public class ContainerOutputResolverTests
{
    [Fact]
    public void Resolve_PrefersTheOwningProjectOutput_WhenTheAssemblyComesFromACopiedTestHostOutput()
    {
        // A test host copies referenced assemblies into its own output without the marker files the
        // runtime image needs, so resolution has to fall back to the project's own output.
        string root = Path.Combine(Path.GetTempPath(), $"tf-output-{Guid.NewGuid():N}");
        string projectDirectory = Path.Combine(root, "App");
        string copiedTestHostOutput = Path.Combine(root, "ConsumerTests", "bin", "Debug", CurrentTargetFramework);
        string owningProjectOutput = Path.Combine(projectDirectory, "bin", "Debug", CurrentTargetFramework);

        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(copiedTestHostOutput);
        Directory.CreateDirectory(owningProjectOutput);

        try
        {
            File.WriteAllText(Path.Combine(projectDirectory, $"{AssemblyName}.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(owningProjectOutput, "host.json"), "{}");
            File.WriteAllText(Path.Combine(owningProjectOutput, $"{AssemblyName}.dll"), string.Empty);
            File.WriteAllText(Path.Combine(copiedTestHostOutput, $"{AssemblyName}.dll"), string.Empty);

            ContainerOutput output = ContainerOutputResolver.ResolveFrom(
                typeof(ContainerOutputResolverTests),
                copiedTestHostOutput,
                ["host.json"]);

            Assert.NotEqual(copiedTestHostOutput, output.OutputDirectory);
            Assert.Equal(owningProjectOutput, output.OutputDirectory);
            Assert.Equal(copiedTestHostOutput, output.InitialOutputDirectory);
            Assert.True(output.UsedFallbackOutput);
            Assert.Contains("copied build location", output.FallbackReason!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_UsesTheLoadedOutput_WhenItAlreadyContainsTheRequiredFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), $"tf-output-{Guid.NewGuid():N}");
        string projectDirectory = Path.Combine(root, "App");
        string output = Path.Combine(projectDirectory, "bin", "Debug", CurrentTargetFramework);

        Directory.CreateDirectory(output);

        try
        {
            File.WriteAllText(Path.Combine(projectDirectory, $"{AssemblyName}.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(output, "host.json"), "{}");
            File.WriteAllText(Path.Combine(output, $"{AssemblyName}.dll"), string.Empty);

            ContainerOutput resolved = ContainerOutputResolver.ResolveFrom(typeof(ContainerOutputResolverTests), output, ["host.json"]);

            Assert.Equal(output, resolved.OutputDirectory);
            Assert.False(resolved.UsedFallbackOutput);
            Assert.Null(resolved.FallbackReason);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_FailsWithEveryPathItExamined_WhenTheRequiredFilesAreMissing()
    {
        string root = Path.Combine(Path.GetTempPath(), $"tf-output-{Guid.NewGuid():N}");
        string projectDirectory = Path.Combine(root, "App");
        string output = Path.Combine(projectDirectory, "bin", "Debug", CurrentTargetFramework);

        Directory.CreateDirectory(output);

        try
        {
            File.WriteAllText(Path.Combine(projectDirectory, $"{AssemblyName}.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(output, $"{AssemblyName}.dll"), string.Empty);

            FrameworkConfigurationException exception = Assert.Throws<FrameworkConfigurationException>(
                () => ContainerOutputResolver.ResolveFrom(typeof(ContainerOutputResolverTests), output, ["host.json"]));

            Assert.Contains("host.json", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Build or publish", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(projectDirectory, exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveTargetFramework_ReturnsTheMonikerTheAssemblyWasBuiltFor()
    {
        // The runtime image is chosen from this, so a hardcoded moniker would break a net10 app.
        string moniker = ContainerOutputResolver.ResolveTargetFramework(typeof(ContainerOutputResolverTests).Assembly);

        Assert.StartsWith("net", moniker, StringComparison.Ordinal);
        Assert.Equal(CurrentTargetFramework, moniker);
    }

    [Fact]
    public void Resolve_UsesTheReleaseConfiguration_WhenTheLoadedOutputCameFromRelease()
    {
        string root = Path.Combine(Path.GetTempPath(), $"tf-output-{Guid.NewGuid():N}");
        string projectDirectory = Path.Combine(root, "App");
        string loaded = Path.Combine(root, "Other", "bin", "Release", CurrentTargetFramework);
        string releaseOutput = Path.Combine(projectDirectory, "bin", "Release", CurrentTargetFramework);

        Directory.CreateDirectory(loaded);
        Directory.CreateDirectory(releaseOutput);

        try
        {
            File.WriteAllText(Path.Combine(projectDirectory, $"{AssemblyName}.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(releaseOutput, "host.json"), "{}");
            File.WriteAllText(Path.Combine(releaseOutput, $"{AssemblyName}.dll"), string.Empty);

            ContainerOutput resolved = ContainerOutputResolver.ResolveFrom(typeof(ContainerOutputResolverTests), loaded, ["host.json"]);

            Assert.Equal(releaseOutput, resolved.OutputDirectory);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static string AssemblyName => typeof(ContainerOutputResolverTests).Assembly.GetName().Name!;

    private static string CurrentTargetFramework => ContainerOutputResolver.ResolveTargetFramework(typeof(ContainerOutputResolverTests).Assembly);
}
