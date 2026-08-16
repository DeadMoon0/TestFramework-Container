using System;
using TestFramework.Container.Azure;
using Xunit;

namespace TestFramework.Container.Azure.Tests;

/// <summary>
/// Covers which Functions host image a payload is mounted into.
/// </summary>
/// <remarks>
/// The image bundles exactly one .NET runtime and the payload is mounted into it, so the two have to
/// agree. When they did not, nothing said so: the container started, the host started, the worker
/// exited with code 150, and the host then answered nothing until the readiness timeout expired four
/// minutes later. The only trace of the real reason -- "To install missing framework ... 10.0.0" --
/// was inside the container log, which nobody reads until they have already lost the four minutes.
///
/// These run without Docker on purpose. The mapping is the part that was wrong, and it is decidable
/// from a string.
/// </remarks>
public class FunctionAppHostImageTests
{
    [Theory]
    [InlineData("net8.0", "mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated8.0")]
    [InlineData("net9.0", "mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated9.0")]
    [InlineData("net10.0", "mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated10.0")]
    public void HostImage_FollowsTheFrameworkThePayloadWasBuiltFor(string targetFramework, string expected)
        => Assert.Equal(expected, DockerAzureDefaults.FunctionAppImageFor(targetFramework));

    [Fact]
    public void TheOldDefault_IsWhatANet8PayloadNowResolvesTo()
    {
        // The pinned constant stays correct for the framework it was always right for, so nothing
        // that targets net8.0 changes image.
        Assert.Equal(DockerAzureDefaults.FunctionAppImage, DockerAzureDefaults.FunctionAppImageFor("net8.0"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("net48")]
    [InlineData("netstandard2.0")]
    [InlineData("not-a-framework")]
    public void AFrameworkWithNoHostImage_IsDeclinedRatherThanGuessed(string? targetFramework)
    {
        // Returning null leaves the declared image alone. Inventing a tag would turn "no image for
        // this" into a pull failure naming an image that never existed.
        Assert.Null(DockerAzureDefaults.FunctionAppImageFor(targetFramework));
    }

    [Fact]
    public void EveryDerivedImage_ComesFromTheIsolatedRepository()
    {
        // A derived tag is pulled without anyone having typed it, so it must not be able to point
        // somewhere else.
        foreach (string framework in new[] { "net8.0", "net9.0", "net10.0", "net11.0" })
        {
            string image = DockerAzureDefaults.FunctionAppImageFor(framework)!;
            Assert.StartsWith(DockerAzureDefaults.FunctionAppImageRepository + ":", image, StringComparison.Ordinal);
        }
    }
}
