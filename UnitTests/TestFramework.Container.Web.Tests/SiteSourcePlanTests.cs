using System;
using System.IO;
using System.Linq;
using TestFramework.Container.Web.Sites;
using TestFramework.Core.Exceptions;
using Xunit;

namespace TestFramework.Container.Web.Tests;

/// <summary>
/// Covers site source planning: what a plan states, and the declarations it demands instead of
/// guessing.
/// </summary>
public class SiteSourcePlanTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"tf-site-plan-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is untidy, never worth failing a test run over.
        }
    }

    [Fact]
    public void Plan_OfAnImage_JustNamesIt()
    {
        SiteSourcePlan plan = SiteSourceResolver.Plan(SiteSource.Image("shop-ui:ci-1234"));

        Assert.Equal(SiteSourceKind.Image, plan.Kind);
        Assert.Equal("shop-ui:ci-1234", plan.Image);
        Assert.Contains("image shop-ui:ci-1234", plan.ToLogLines("shop")[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_OfADirectory_ReportsWhenTheIndexWasWritten()
    {
        string dist = CreateDist("dist");

        SiteSourcePlan plan = SiteSourceResolver.Plan(SiteSource.Directory(dist));

        Assert.Equal(SiteSourceKind.Directory, plan.Kind);
        Assert.Equal(dist, plan.DistDirectory);
        Assert.NotNull(plan.BuiltAtUtc);
        Assert.Contains(plan.Derivations, derivation => derivation.Contains("nothing checks it is current", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_OfADirectoryWithoutAnIndex_SaysSoNow()
    {
        string empty = Directory.CreateDirectory(Path.Combine(_root, "empty")).FullName;

        FrameworkConfigurationException exception = Assert.Throws<FrameworkConfigurationException>(
            () => SiteSourceResolver.Plan(SiteSource.Directory(empty)));

        Assert.Contains("index.html", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_OfAnNpmProjectWithoutAPackageJson_SaysSoNow()
    {
        FrameworkConfigurationException exception = Assert.Throws<FrameworkConfigurationException>(
            () => SiteSourceResolver.Plan(SiteSource.NpmProject(_root)));

        Assert.Contains("package.json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_OfAnNpmProject_TakesTheDeclaredDistPath()
    {
        CreateNpmProject();

        SiteSourcePlan plan = SiteSourceResolver.Plan(SiteSource.NpmProject(_root).WithDistPath("out/site"));

        Assert.Equal(Path.Combine(_root, "out", "site"), plan.DistDirectory);
        Assert.Empty(plan.Derivations);
        Assert.Equal("npm run build", plan.BuildCommand);
    }

    [Fact]
    public void Plan_OfAnNpmProject_ProbesTheAngularLayoutFirst()
    {
        CreateNpmProject();
        string dist = CreateDist(Path.Combine("dist", "sample", "browser"));

        SiteSourcePlan plan = SiteSourceResolver.Plan(SiteSource.NpmProject(_root));

        Assert.Equal(dist, plan.DistDirectory);
        Assert.Contains(plan.Derivations, derivation => derivation.Contains("probed by convention", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_OfAnNpmProject_RefusesTwoPlausibleDistFolders()
    {
        CreateNpmProject();
        CreateDist(Path.Combine("dist", "one", "browser"));
        CreateDist(Path.Combine("dist", "two", "browser"));

        FrameworkConfigurationException exception = Assert.Throws<FrameworkConfigurationException>(
            () => SiteSourceResolver.Plan(SiteSource.NpmProject(_root)));

        Assert.Contains("more than one plausible", exception.Message, StringComparison.Ordinal);
        Assert.Contains("WithDistPath", string.Join(" ", exception.RecoverySteps), StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_OfAnNpmProject_AsksForADeclarationWhenNothingIsBuiltYet()
    {
        CreateNpmProject();

        FrameworkConfigurationException exception = Assert.Throws<FrameworkConfigurationException>(
            () => SiteSourceResolver.Plan(SiteSource.NpmProject(_root)));

        Assert.Contains("WithDistPath", string.Join(" ", exception.RecoverySteps), StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_OfAContainerBuild_DemandsADeclaredDistPath()
    {
        FrameworkConfigurationException exception = Assert.Throws<FrameworkConfigurationException>(
            () => SiteSourceResolver.Plan(SiteSource.ContainerBuild(_root, "hugomods/hugo", ["hugo"])));

        Assert.Contains("WithDistPath", string.Join(" ", exception.RecoverySteps), StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_OfAContainerBuild_StatesTheBuildAndItsImage()
    {
        SiteSourcePlan plan = SiteSourceResolver.Plan(
            SiteSource.ContainerBuild(_root, "hugomods/hugo", ["hugo", "--minify"]).WithDistPath("public"));

        Assert.Equal(SiteSourceKind.ContainerBuild, plan.Kind);
        Assert.Equal("hugo --minify", plan.BuildCommand);
        Assert.Equal("hugomods/hugo", plan.BuildImage);

        string buildLine = Assert.Single(plan.ToLogLines("docs"), line => line.Contains("build", StringComparison.Ordinal) && line.Contains("hugo", StringComparison.Ordinal));
        Assert.Contains("(in hugomods/hugo)", buildLine, StringComparison.Ordinal);
    }

    private void CreateNpmProject()
        => File.WriteAllText(Path.Combine(_root, "package.json"), "{ \"scripts\": { \"build\": \"echo build\" } }");

    private string CreateDist(string relative)
    {
        string directory = Directory.CreateDirectory(Path.Combine(_root, relative)).FullName;
        File.WriteAllText(Path.Combine(directory, "index.html"), "<!doctype html><title>t</title>");
        return directory;
    }
}
