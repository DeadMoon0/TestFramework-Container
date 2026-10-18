using System;
using System.IO;
using TestFramework.Container.Sources;
using Xunit;

namespace TestFramework.Container.Tests;

/// <summary>
/// An image is reused only while the sources it was built from are unchanged - the application's and every project
/// it references.
/// </summary>
/// <remarks>
/// The cache used to key on the project path, configuration, framework and base image alone, so a test host kept
/// running across an edit handed every later run the image of the first build, with a log line calling it fresh.
/// </remarks>
public sealed class SourceFingerprintTests : IDisposable
{
    private static readonly DateTime Built = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tf-fingerprint-{Guid.NewGuid():N}");

    public SourceFingerprintTests()
    {
        // App -> Shared -> Core, and an unrelated Tool beside them.
        Project("App", references: "..\\Shared\\Shared.csproj");
        Project("Shared", references: "../Core/Core.csproj");
        Project("Core");
        Project("Tool");
    }

    [Fact]
    public void AnEditToTheApplication_ChangesWhatCountsAsTheSameBuild()
        => AssertChangesFingerprint(Path.Combine("App", "Program.cs"));

    [Fact]
    public void AnEditToAReferencedProject_ChangesWhatCountsAsTheSameBuild()
        => AssertChangesFingerprint(Path.Combine("Shared", "Program.cs"));

    [Fact]
    public void AnEditToAProjectReferencedThroughAnother_ChangesWhatCountsAsTheSameBuild()
        => AssertChangesFingerprint(Path.Combine("Core", "Program.cs"));

    [Fact]
    public void DeletingASourceFile_ChangesWhatCountsAsTheSameBuild()
    {
        Write(Path.Combine("App", "Helper.cs"), "class H { }");
        string before = SourceFingerprint.Of(AppProject);

        File.Delete(Path.Combine(_root, "App", "Helper.cs"));

        Assert.NotEqual(before, SourceFingerprint.Of(AppProject));
    }

    [Fact]
    public void ABuildWritingItsOutput_DoesNotChangeWhatCountsAsTheSameBuild()
    {
        string before = SourceFingerprint.Of(AppProject);

        Touch(Path.Combine("App", "bin", "Release", "App.dll"));
        Touch(Path.Combine("App", "obj", "project.assets.json"));

        Assert.Equal(before, SourceFingerprint.Of(AppProject));
    }

    [Fact]
    public void AnEditToAnUnrelatedProject_DoesNotChangeWhatCountsAsTheSameBuild()
    {
        string before = SourceFingerprint.Of(AppProject);

        Touch(Path.Combine("Tool", "Program.cs"));

        Assert.Equal(before, SourceFingerprint.Of(AppProject));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string AppProject => Path.Combine(_root, "App", "App.csproj");

    private void AssertChangesFingerprint(string relativeFile)
    {
        string before = SourceFingerprint.Of(AppProject);

        Touch(relativeFile);

        Assert.NotEqual(before, SourceFingerprint.Of(AppProject));
    }

    private void Project(string name, string? references = null)
    {
        string reference = references is null ? string.Empty : $"<ItemGroup><ProjectReference Include=\"{references}\" /></ItemGroup>";
        Write(Path.Combine(name, $"{name}.csproj"), $"<Project Sdk=\"Microsoft.NET.Sdk\">{reference}</Project>");
        Write(Path.Combine(name, "Program.cs"), "class C { }");
    }

    private void Write(string relative, string content)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, Built);
    }

    /// <summary>An edit an hour after the build, stated rather than waited for.</summary>
    private void Touch(string relative)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "changed");
        File.SetLastWriteTimeUtc(path, Built.AddHours(1));
    }
}
