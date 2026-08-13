using System;
using System.IO;
using TestFramework.Container.Web.SampleApi;
using Xunit;

namespace TestFramework.Container.Web.Tests;

/// <summary>
/// Covers which directory is shipped into an application container.
/// </summary>
/// <remarks>
/// A project-referenced assembly is copied into the referencing project's output too, so resolving
/// from the loaded location lands in this test project's bin: complete enough to start, twice the
/// size, and misleading about what actually ran. This is the regression test for that.
/// </remarks>
public class ShippedApiOutputTests
{
    [Fact]
    public void ResolveProjectOutput_ShipsTheApplicationsOwnOutput_NotTheTestProjectsCopy()
    {
        ContainerOutput output = ContainerOutputResolver.ResolveProjectOutput(typeof(SampleApiMarker));

        string testOutputDirectory = Path.GetDirectoryName(typeof(ShippedApiOutputTests).Assembly.Location)!;

        Assert.NotEqual(testOutputDirectory, output.OutputDirectory);
        Assert.Contains("TestFramework.Container.Web.SampleApi", output.ProjectDirectory, StringComparison.Ordinal);
        Assert.Contains("TestFramework.Container.Web.SampleApi", output.OutputDirectory, StringComparison.Ordinal);
        Assert.Equal("TestFramework.Container.Web.SampleApi.dll", output.AssemblyFileName);
        Assert.False(output.UsedFallbackOutput);
    }

    [Fact]
    public void ResolveProjectOutput_ReportsTheFrameworkTheImageIsChosenFrom()
    {
        ContainerOutput output = ContainerOutputResolver.ResolveProjectOutput(typeof(SampleApiMarker));

        Assert.StartsWith("net", output.TargetFramework, StringComparison.Ordinal);
        Assert.EndsWith(output.TargetFramework, output.OutputDirectory, StringComparison.Ordinal);
    }
}
