using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Container.Sources;
using TestFramework.Core.Exceptions;
using Xunit;

namespace TestFramework.Container.Tests;

/// <summary>
/// Covers the files and the feed an in-container build is made of, without running Docker.
/// </summary>
public class OfflineBuildTests
{
    [Fact]
    public void Dockerfile_RestoresFromTheCopiedPackagesAndNowhereElse()
    {
        string dockerfile = DockerfileGenerator.WriteDockerfile(
            "mcr.microsoft.com/dotnet/sdk:10.0",
            "mcr.microsoft.com/dotnet/aspnet:10.0",
            @"Orders.Api\Orders.Api.csproj",
            "Orders.Api.dll",
            "Release",
            "net10.0");

        Assert.Contains("FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build", dockerfile, StringComparison.Ordinal);
        Assert.Contains("COPY packages/ /packages/", dockerfile, StringComparison.Ordinal);
        Assert.Contains("ENV NUGET_PACKAGES=/packages", dockerfile, StringComparison.Ordinal);
        Assert.Contains("--configfile ./NuGet.config", dockerfile, StringComparison.Ordinal);
        Assert.Contains("--no-restore", dockerfile, StringComparison.Ordinal);
        Assert.Contains("ENTRYPOINT [\"dotnet\", \"Orders.Api.dll\"]", dockerfile, StringComparison.Ordinal);

        // A Windows path would not resolve inside the container.
        Assert.Contains("\"Orders.Api/Orders.Api.csproj\"", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain('\\', dockerfile);
    }

    [Fact]
    public void Dockerfile_UsesNewlinesWhateverTheHostDoes()
    {
        string dockerfile = DockerfileGenerator.WriteDockerfile("sdk", "runtime", "a/b.csproj", "b.dll", "Release", "net8.0");

        Assert.DoesNotContain('\r', dockerfile);
        Assert.EndsWith("\n", dockerfile, StringComparison.Ordinal);
    }

    [Fact]
    public void Dockerfile_TurnsOffTheAuditThatWouldNeedTheNetwork()
    {
        string dockerfile = DockerfileGenerator.WriteDockerfile("sdk", "runtime", "a/b.csproj", "b.dll", "Release", "net8.0");

        // An offline restore cannot fetch vulnerability data, and the warning becomes an error in a
        // project that treats warnings as errors.
        Assert.Equal(2, dockerfile.Split("-p:NuGetAudit=false").Length - 1);

        // The native launcher is a per-platform package, so a feed built on Windows would not hold
        // the one a Linux build asks for.
        Assert.Equal(2, dockerfile.Split("-p:UseAppHost=false").Length - 1);
    }

    [Fact]
    public void NuGetConfig_ClearsEverySourceSoNoCredentialIsNeeded()
    {
        string config = DockerfileGenerator.WriteNuGetConfig();

        Assert.Contains("<clear />", config, StringComparison.Ordinal);
        Assert.DoesNotContain("<add", config, StringComparison.Ordinal);
        Assert.DoesNotContain("http", config, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DockerIgnore_KeepsBuildOutputOutOfTheContext()
    {
        string ignore = DockerfileGenerator.WriteDockerIgnore(["**/secrets/"]);

        Assert.Contains("**/bin/", ignore, StringComparison.Ordinal);
        Assert.Contains("**/obj/", ignore, StringComparison.Ordinal);
        Assert.Contains("**/secrets/", ignore, StringComparison.Ordinal);
    }

    [Fact]
    public void CopySources_SkipsWhatABuildProduces()
    {
        string root = Path.Combine(Path.GetTempPath(), $"tf-copy-{Guid.NewGuid():N}");
        string source = Path.Combine(root, "src");
        string destination = Path.Combine(root, "out");

        Directory.CreateDirectory(Path.Combine(source, "App", "bin", "Release"));
        Directory.CreateDirectory(Path.Combine(source, "App", "obj"));
        Directory.CreateDirectory(Path.Combine(source, "App", "Controllers"));

        try
        {
            File.WriteAllText(Path.Combine(source, "App", "App.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(source, "App", "Controllers", "Handler.cs"), "class Handler {}");
            File.WriteAllText(Path.Combine(source, "App", "bin", "Release", "App.dll"), string.Empty);
            File.WriteAllText(Path.Combine(source, "App", "obj", "project.assets.json"), "{}");

            int copied = InContainerBuild.CopySources(source, destination);

            Assert.Equal(2, copied);
            Assert.True(File.Exists(Path.Combine(destination, "App", "App.csproj")));
            Assert.True(File.Exists(Path.Combine(destination, "App", "Controllers", "Handler.cs")));
            Assert.False(Directory.Exists(Path.Combine(destination, "App", "bin")));
            Assert.False(Directory.Exists(Path.Combine(destination, "App", "obj")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CopySources_FailsWhenThereIsNothingToCopy()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"tf-absent-{Guid.NewGuid():N}");

        Assert.Throws<FrameworkConfigurationException>(() => InContainerBuild.CopySources(missing, Path.Combine(missing, "out")));
    }

    [Fact]
    public async Task OfflineFeed_CollectsWhatTheHostResolvedForThisProject()
    {
        ProjectFacts facts = await ProjectQuery.ReadAsync(SampleApiProject, CancellationToken.None);
        string destination = Path.Combine(Path.GetTempPath(), $"tf-feed-{Guid.NewGuid():N}");

        try
        {
            OfflineFeedResult feed = await OfflineFeed.CreateAsync(facts, "net8.0", destination, CancellationToken.None);

            Assert.NotEmpty(feed.Packages);
            Assert.Empty(feed.Missing);
            Assert.Contains(feed.Packages, package => package.Id.Equals("Microsoft.Data.SqlClient", StringComparison.OrdinalIgnoreCase));

            // Handed over as extracted id/version folders, which is what a populated cache looks
            // like; several versions of one package share an id folder.
            int versionFolders = Directory.GetDirectories(destination).Sum(id => Directory.GetDirectories(id).Length);
            Assert.Equal(feed.Packages.Count, versionFolders);

            // The targeting packs travel too, because an SDK image only bundles its own generation.
            Assert.Contains(feed.Packages, package => package.Id.Contains("app.ref", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(destination))
                Directory.Delete(destination, recursive: true);
        }
    }

    private static string SampleApiProject
    {
        get
        {
            for (DirectoryInfo? current = new(Path.GetDirectoryName(typeof(OfflineBuildTests).Assembly.Location)!);
                 current is not null;
                 current = current.Parent)
            {
                if (current.EnumerateFiles("*.slnx").Any())
                    return Path.Combine(current.FullName, "UnitTests", "TestFramework.Container.Web.SampleApi", "TestFramework.Container.Web.SampleApi.csproj");
            }

            throw new InvalidOperationException("The repository root could not be located from the test assembly.");
        }
    }
}
