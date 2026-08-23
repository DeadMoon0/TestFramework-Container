using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Container.Sources;
using TestFramework.Core.Logging;
using Xunit;

namespace TestFramework.Container.Tests;

/// <summary>
/// Covers what happens when several environments want the same project at the same moment.
/// </summary>
/// <remarks>
/// No Docker anywhere in here: publishing to a directory is the one build strategy that never talks
/// to a daemon, which is also why it is the one a Function App has to use.
/// </remarks>
public class ContainerBuildGateTests
{
    /// <summary>
    /// Three callers, one project, one publish at a time.
    /// </summary>
    /// <remarks>
    /// Without the gate this fails, and it fails the way the real thing did: the outputs never
    /// collide, because each caller is handed its own directory, but they drive MSBuild over one
    /// project and the losers report that the project could not be published. Three because two is
    /// close enough to a coincidence to pass on a quiet machine.
    ///
    /// The project is generated here rather than borrowed from the repository, and that is the part
    /// that makes the test mean anything. An already-built project publishes incrementally, does
    /// almost nothing in <c>obj/</c>, and races too briefly to lose - so pointing this at a project
    /// the rest of the suite has already built produces a test that passes with the fix reverted.
    /// A fresh project restores and compiles, which is where the contention actually lives.
    /// </remarks>
    [Fact]
    public async Task HostPublish_LetsSeveralCallersShareOneProjectWithoutRacingItsBuildDirectory()
    {
        string projectDirectory = CreateColdProject();

        try
        {
            ContainerSourcePlan plan = await ContainerSourceResolver.PlanAsync(
                ContainerSource.Project(Path.Combine(projectDirectory, "Gated.csproj")).BuiltOnHost(),
                CancellationToken.None);

            Assert.Equal(ContainerBuildStrategy.HostPublish, plan.Strategy);

            ScopedLogger logger = CreateLogger();

            ContainerSourcePlan[] built = await Task.WhenAll(
                Enumerable
                    .Range(0, 3)
                    .Select(index => ContainerImageBuilder.BuildAsync(plan, $"gated-app-{index}", logger, CancellationToken.None)));

            IReadOnlyList<string> directories = [.. built.Select(result => result.OutputDirectory!)];

            // Serialised, not shared. Each environment deletes its own output at teardown, so handing
            // two of them one directory would hand the second one a directory about to be removed.
            Assert.Equal(3, directories.Distinct(StringComparer.Ordinal).Count());

            foreach (string directory in directories)
                Assert.True(Directory.Exists(directory), $"'{directory}' was reported as published but is not there.");

            foreach (ContainerSourcePlan result in built)
                Delete(result.OutputDirectory);
        }
        finally
        {
            Delete(projectDirectory);
        }
    }

    /// <summary>
    /// Writes the smallest project that still restores and compiles, somewhere nothing else builds.
    /// </summary>
    private static string CreateColdProject()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"tf-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, "Gated.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <OutputType>Exe</OutputType>
                <Nullable>enable</Nullable>
                <ImplicitUsings>disable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(directory, "Program.cs"), "System.Console.WriteLine(\"gated\");");

        return directory;
    }

    private static void Delete(string? directory)
    {
        try
        {
            if (directory is not null && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary directory is untidy, never a reason to fail a test.
        }
    }

    /// <summary>
    /// Builds a logger that writes nowhere, which is all this needs.
    /// </summary>
    /// <remarks>
    /// ScopedLogger is public but has no public constructor, because a run hands one out rather than
    /// letting anything make its own. A test is the one place that has to.
    /// </remarks>
    private static ScopedLogger CreateLogger()
        => (ScopedLogger)Activator.CreateInstance(
            typeof(ScopedLogger),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: [null],
            culture: null)!;
}
