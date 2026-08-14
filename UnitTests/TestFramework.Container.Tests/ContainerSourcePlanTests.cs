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
/// Covers what a declared source resolves to, before anything is built or started.
/// </summary>
/// <remarks>
/// Planning is deliberately side-effect free, so all of this runs without Docker.
/// </remarks>
public class ContainerSourcePlanTests
{
    private static string ThisProject => Path.Combine(RepositoryRoot, "UnitTests", "TestFramework.Container.Tests", "TestFramework.Container.Tests.csproj");

    private static string SampleApiProject => Path.Combine(RepositoryRoot, "UnitTests", "TestFramework.Container.Web.SampleApi", "TestFramework.Container.Web.SampleApi.csproj");

    [Fact]
    public async Task Image_PlansToRunItWithNothingDerived()
    {
        ContainerSourcePlan plan = await ContainerSourceResolver.PlanAsync(ContainerSource.Image("orders-api:ci-1234"), CancellationToken.None);

        Assert.Equal(ContainerSourceKind.Image, plan.Kind);
        Assert.Equal("orders-api:ci-1234", plan.Image);
        Assert.Empty(plan.Derivations);
        Assert.Null(plan.ProjectPath);
    }

    [Fact]
    public async Task Project_ReadsTheFrameworkAndImageFromTheProjectItself()
    {
        ContainerSourcePlan plan = await ContainerSourceResolver.PlanAsync(
            ContainerSource.Project(SampleApiProject).WithTargetFramework("net8.0"),
            CancellationToken.None);

        Assert.Equal(ContainerSourceKind.Project, plan.Kind);
        Assert.Equal(ContainerBuildStrategy.SdkContainerPublish, plan.Strategy);
        Assert.Equal("Release", plan.Configuration);
        Assert.Equal("net8.0", plan.TargetFramework);

        // The sample is a web project, so it lands on aspnet rather than the plain runtime.
        Assert.Equal("mcr.microsoft.com/dotnet/aspnet:8.0", plan.RuntimeImage);
        Assert.Equal("TestFramework.Container.Web.SampleApi.dll", plan.AssemblyFileName);
        Assert.Contains(plan.Derivations, note => note.Contains("web SDK", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Project_RefusesToChooseWhenTheProjectTargetsSeveralFrameworks()
    {
        FrameworkConfigurationException exception = await Assert.ThrowsAsync<FrameworkConfigurationException>(
            () => ContainerSourceResolver.PlanAsync(ContainerSource.Project(SampleApiProject), CancellationToken.None));

        // Choosing silently would mean adding a framework to a project quietly changes what runs.
        Assert.Contains("ambiguous", exception.Message, StringComparison.Ordinal);
        Assert.Contains("net8.0", exception.Message, StringComparison.Ordinal);
        Assert.Contains("net10.0", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Project_RefusesAFrameworkTheProjectDoesNotTarget()
    {
        FrameworkConfigurationException exception = await Assert.ThrowsAsync<FrameworkConfigurationException>(
            () => ContainerSourceResolver.PlanAsync(
                ContainerSource.Project(SampleApiProject).WithTargetFramework("net6.0"),
                CancellationToken.None));

        Assert.Contains("does not target 'net6.0'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Project_BuiltInContainer_DerivesTheContextAndTheSdkImage()
    {
        ContainerSourcePlan plan = await ContainerSourceResolver.PlanAsync(
            ContainerSource.Project(SampleApiProject).WithTargetFramework("net10.0").BuiltInContainer(),
            CancellationToken.None);

        Assert.Equal(ContainerBuildStrategy.InContainer, plan.Strategy);
        Assert.NotNull(plan.ContextDirectory);
        Assert.StartsWith("mcr.microsoft.com/dotnet/sdk:", plan.SdkImage!, StringComparison.Ordinal);

        // Matched to the host SDK, because package pruning differs between SDK versions and the
        // handed-over cache only satisfies a restore that resolves the same set.
        Assert.Contains(plan.Derivations, note => note.Contains("SDK on this machine", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Project_HonoursExplicitImagesAndContext()
    {
        ContainerSourcePlan plan = await ContainerSourceResolver.PlanAsync(
            ContainerSource.Project(SampleApiProject)
                .WithTargetFramework("net10.0")
                .BuiltInContainer()
                .WithRuntimeImage("my-registry/runtime:1")
                .WithSdkImage("my-registry/sdk:1")
                .WithContext(RepositoryRoot),
            CancellationToken.None);

        Assert.Equal("my-registry/runtime:1", plan.RuntimeImage);
        Assert.Equal("my-registry/sdk:1", plan.SdkImage);
        Assert.Equal(Path.GetFullPath(RepositoryRoot), plan.ContextDirectory);
    }

    [Fact]
    public async Task Project_FailsClearlyWhenTheProjectDoesNotExist()
    {
        FrameworkConfigurationException exception = await Assert.ThrowsAsync<FrameworkConfigurationException>(
            () => ContainerSourceResolver.PlanAsync(
                ContainerSource.Project(Path.Combine(RepositoryRoot, "NoSuch", "NoSuch.csproj")),
                CancellationToken.None));

        Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_ResolvesARelativePathAgainstTheFileThatDeclaredIt()
    {
        // This test file lives in the test project directory, so the sibling path below is the same
        // one a reader sees in the repository.
        ProjectContainerSource source = ContainerSource.Project("../TestFramework.Container.Web.SampleApi/TestFramework.Container.Web.SampleApi.csproj");

        Assert.Equal(Path.GetFullPath(SampleApiProject), source.ProjectPath);
    }

    [Fact]
    public async Task Directory_ShipsWhatIsThereAndSaysHowOldItIs()
    {
        string output = Path.GetDirectoryName(typeof(ContainerSourcePlanTests).Assembly.Location)!;

        ContainerSourcePlan plan = await ContainerSourceResolver.PlanAsync(ContainerSource.Directory(output), CancellationToken.None);

        Assert.Equal(ContainerSourceKind.Directory, plan.Kind);
        Assert.Equal(output, plan.OutputDirectory);
        Assert.NotNull(plan.AssemblyFileName);
        Assert.NotNull(plan.BuiltAtUtc);
    }

    [Fact]
    public async Task Directory_FailsWhenThereIsNothingToShip()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"tf-missing-{Guid.NewGuid():N}");

        FrameworkConfigurationException exception = await Assert.ThrowsAsync<FrameworkConfigurationException>(
            () => ContainerSourceResolver.PlanAsync(ContainerSource.Directory(missing), CancellationToken.None));

        Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntryPoint_StillWorksAndSaysWhatItInferred()
    {
        ContainerSourcePlan plan = await ContainerSourceResolver.PlanAsync(
            ContainerSource.EntryPoint<ContainerSourcePlanTests>(),
            CancellationToken.None);

        Assert.Equal(ContainerSourceKind.EntryPoint, plan.Kind);
        Assert.NotNull(plan.OutputDirectory);

        // Every inference it makes is named, which is the whole difference from before.
        Assert.Contains(plan.Derivations, note => note.Contains("loaded assembly", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ToLogLines_StatesThePlanBeforeAnythingRuns()
    {
        ContainerSourcePlan plan = await ContainerSourceResolver.PlanAsync(
            ContainerSource.Project(SampleApiProject).WithTargetFramework("net8.0"),
            CancellationToken.None);

        string rendered = string.Join(Environment.NewLine, plan.ToLogLines("orders"));

        Assert.Contains("'orders' source plan", rendered, StringComparison.Ordinal);
        Assert.Contains("Project (image built by the SDK)", rendered, StringComparison.Ordinal);
        Assert.Contains("net8.0", rendered, StringComparison.Ordinal);
        Assert.Contains("aspnet:8.0", rendered, StringComparison.Ordinal);
        Assert.Contains("derived", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectQuery_ReadsWhatTheProjectDeclares()
    {
        ProjectFacts facts = await ProjectQuery.ReadAsync(SampleApiProject, CancellationToken.None);

        Assert.Equal("TestFramework.Container.Web.SampleApi", facts.AssemblyName);
        Assert.Equal(["net8.0", "net10.0"], facts.TargetFrameworks);
        Assert.True(facts.IsWebSdk);
        Assert.Equal("mcr.microsoft.com/dotnet/aspnet", facts.RuntimeImageRepository);
    }

    [Fact]
    public async Task ResolveCommonRoot_CoversTheProjectAndEverythingItReferences()
    {
        ProjectFacts facts = await ProjectQuery.ReadAsync(ThisProject, CancellationToken.None);

        string root = ProjectQuery.ResolveCommonRoot(facts);

        // This test project references the container packages, so the context has to reach above it.
        Assert.NotEmpty(facts.ProjectReferences);
        Assert.StartsWith(root, facts.ProjectDirectory, StringComparison.OrdinalIgnoreCase);
        foreach (string reference in facts.ProjectReferences)
            Assert.StartsWith(root, reference, StringComparison.OrdinalIgnoreCase);
    }

    private static string RepositoryRoot
    {
        get
        {
            // Walks up from the test assembly to the repository, which is the one anchor that holds
            // on both a Windows and a Linux checkout.
            for (DirectoryInfo? current = new(Path.GetDirectoryName(typeof(ContainerSourcePlanTests).Assembly.Location)!);
                 current is not null;
                 current = current.Parent)
            {
                if (current.EnumerateFiles("*.slnx").Any() || current.EnumerateDirectories(".git").Any())
                    return current.FullName;
            }

            throw new InvalidOperationException("The repository root could not be located from the test assembly.");
        }
    }
}
