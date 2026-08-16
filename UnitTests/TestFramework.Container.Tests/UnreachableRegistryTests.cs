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
/// Covers what happens when the registry holding the base image cannot be reached.
/// </summary>
/// <remarks>
/// This is not a rare corner. The SDK's container publish resolves the base image's manifest from a
/// registry on every build, even when that image is already in the local daemon, and a broken IPv6
/// path breaks exactly that request while leaving everything else working. A PPPoE line -- the
/// normal shape of consumer DSL, whose MTU is 1492 rather than 1500 -- behind a VPN is enough. On
/// such a machine the SDK spends about two and a half minutes retrying and then fails with
/// CONTAINER1006, which says nothing about the cause.
///
/// The tests that need Docker only need it locally: the route under test is the one that contacts no
/// registry, which is what makes it verifiable on the very machine the problem was found on.
/// </remarks>
public class UnreachableRegistryTests
{
    [Fact]
    public void UnreachableRegistry_IsToldApartFromABrokenProject()
    {
        // The shape the SDK actually produces on the network this was written for.
        Assert.True(ContainerImageBuilder.IsRegistryUnreachable(Failure(
            "  Failed to download from \"https://mcr.microsoft.com/v2/dotnet/aspnet/manifests/8.0\".",
            "error CONTAINER1006: Failed to download from \"https://mcr.microsoft.com/v2/\".")));

        Assert.True(ContainerImageBuilder.IsRegistryUnreachable(Failure(
            "The SSL connection could not be established, see inner exception.", string.Empty)));

        Assert.True(ContainerImageBuilder.IsRegistryUnreachable(Failure(
            string.Empty, "Unable to read data from the transport connection: An existing connection was forcibly closed.")));

        // A compile error must never take the recovery route: the project is broken, and building it
        // a second way would only fail again more slowly.
        Assert.False(ContainerImageBuilder.IsRegistryUnreachable(Failure(
            "Program.cs(12,9): error CS0103: The name 'Foo' does not exist in the current context", string.Empty)));

        Assert.False(ContainerImageBuilder.IsRegistryUnreachable(Failure(string.Empty, string.Empty)));
    }

    [Fact]
    public void Guidance_NamesTheRegistryAndAWayOutThatNeedsNoAdministrator()
    {
        ContainerSourcePlan plan = new()
        {
            Kind = ContainerSourceKind.Project,
            Strategy = ContainerBuildStrategy.SdkContainerPublish,
            RuntimeImage = "mcr.microsoft.com/dotnet/aspnet:8.0",
        };

        IReadOnlyList<string> steps = ContainerImageBuilder.RecoveryStepsFor(
            Failure(string.Empty, "error CONTAINER1006: Failed to download."), plan);

        string all = string.Join("\n", steps);

        Assert.Contains("mcr.microsoft.com/dotnet/aspnet:8.0", all, StringComparison.Ordinal);

        // The one step that works on a locked-down machine has to be there, because every other one
        // needs either a working network or administrator rights.
        Assert.Contains("ContainerSource.Image(", all, StringComparison.Ordinal);
        Assert.Contains("docker pull", all, StringComparison.Ordinal);

        // A failure that is not the registry gets the short answer instead.
        Assert.DoesNotContain(
            "docker pull",
            string.Join("\n", ContainerImageBuilder.RecoveryStepsFor(Failure("error CS0103", string.Empty), plan)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AWedgedPublish_IsRecoverableAndSaysSoWithoutOutputToQuote()
    {
        // The case that costs the most: the SDK reports nothing at all, because a registry that
        // neither answers nor refuses leaves it waiting rather than failing. Publishes were found
        // still alive after ninety minutes, outliving the test run that started them.
        DotNetCliResult wedged = new(["publish"], -1, string.Empty, "did not finish within 00:10:00") { TimedOut = true };

        ContainerSourcePlan plan = new()
        {
            Kind = ContainerSourceKind.Project,
            RuntimeImage = "mcr.microsoft.com/dotnet/aspnet:8.0",
        };

        string steps = string.Join("\n", ContainerImageBuilder.RecoveryStepsFor(wedged, plan));

        // Not the same message as a failure: there is no output to read, so pointing at the log
        // would be pointing at nothing.
        Assert.DoesNotContain("The full command output follows", steps, StringComparison.Ordinal);
        Assert.Contains("stopped making progress", steps, StringComparison.Ordinal);
        Assert.Contains("mcr.microsoft.com/dotnet/aspnet:8.0", steps, StringComparison.Ordinal);
        Assert.Contains("ContainerSource.Image(", steps, StringComparison.Ordinal);

        // And a timeout must still reach the recovery, which a plain unreachable-check would miss:
        // there is no CONTAINER1006 in the output of a command that never produced any.
        Assert.False(ContainerImageBuilder.IsRegistryUnreachable(wedged));
        Assert.True(wedged.TimedOut);
    }

    [Fact]
    public async Task AProcessThatOutrunsItsTimeout_ComesBackAsAResultRatherThanAHang()
    {
        // The bound itself, exercised end to end on a command that will not finish in time.
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

        DotNetCliResult result = await DotNetCli.RunAsync(
            ["msbuild", "-h"], null, TimeSpan.FromMilliseconds(1), CancellationToken.None);

        stopwatch.Stop();

        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.Contains("did not finish within", result.StandardError, StringComparison.Ordinal);

        // The point of the bound is that the caller gets its thread back.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"The bounded run took {stopwatch.Elapsed}.");
    }

    [Theory]
    [InlineData("mcr.microsoft.com/dotnet/aspnet:8.0", "mcr.microsoft.com")]
    [InlineData("ghcr.io/owner/app:1.0", "ghcr.io")]
    [InlineData("localhost:5000/app:dev", "localhost:5000")]
    [InlineData("registry.example.com:8443/team/app", "registry.example.com:8443")]
    [InlineData("redis:7", ContainerRegistryProbe.DockerHubHost)]
    [InlineData("library/redis:7", ContainerRegistryProbe.DockerHubHost)]
    [InlineData("my-api:local", ContainerRegistryProbe.DockerHubHost)]
    public void RegistryHost_IsReadFromTheImageReference(string image, string expected)
        => Assert.Equal(expected, ContainerRegistryProbe.RegistryHostOf(image));

    [Fact]
    public async Task UnresolvableHost_IsReportedAsUnreachableRatherThanThrowing()
    {
        // A probe that threw would turn a network hint into a new failure mode of its own.
        ContainerRegistryProbe.Forget();
        try
        {
            Assert.False(await ContainerRegistryProbe.IsReachableAsync(
                "no-such-registry.invalid/dotnet/aspnet:8.0", CancellationToken.None));
        }
        finally
        {
            ContainerRegistryProbe.Forget();
        }
    }

    [Fact]
    public async Task Probe_AnswersOncePerRegistry()
    {
        ContainerRegistryProbe.Forget();
        try
        {
            System.Diagnostics.Stopwatch first = System.Diagnostics.Stopwatch.StartNew();
            bool answer = await ContainerRegistryProbe.IsReachableAsync("unreachable.invalid/app:1", CancellationToken.None);
            first.Stop();

            System.Diagnostics.Stopwatch second = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal(answer, await ContainerRegistryProbe.IsReachableAsync("unreachable.invalid/other:2", CancellationToken.None));
            second.Stop();

            // A run building several images would otherwise pay the timeout again for each one.
            Assert.True(second.Elapsed < TimeSpan.FromMilliseconds(200), $"The remembered answer took {second.Elapsed}.");
        }
        finally
        {
            ContainerRegistryProbe.Forget();
        }
    }

    [Fact]
    public void Dockerfile_MatchesTheShapeTheSdkWouldHaveProduced()
    {
        string dockerfile = DockerfileGenerator.WritePublishedOutputDockerfile("mcr.microsoft.com/dotnet/aspnet:8.0", "Orders.Api.dll");

        // /app and 'dotnet Orders.Api.dll' are what the SDK's own publish produces, and the web
        // component maps its settings file into /app expecting exactly that.
        Assert.Contains("FROM mcr.microsoft.com/dotnet/aspnet:8.0\n", dockerfile, StringComparison.Ordinal);
        Assert.Contains("WORKDIR /app\n", dockerfile, StringComparison.Ordinal);
        Assert.Contains("ENTRYPOINT [\"dotnet\", \"Orders.Api.dll\"]", dockerfile, StringComparison.Ordinal);

        // Read inside a Linux container whatever the host writes.
        Assert.DoesNotContain('\r', dockerfile);

        // Nothing here may reach for a registry beyond the one base image.
        Assert.DoesNotContain("RUN ", dockerfile, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds a real image the way a machine with an unreachable registry would have to.
    /// </summary>
    /// <remarks>
    /// The point of this test is that it passes on a machine where the SDK's own image build cannot:
    /// nothing here contacts a registry, so a broken path to one cannot affect it. It is skipped
    /// where Docker is absent, or where the base image has not been fetched yet -- on that machine
    /// the fallback would not apply either, which is exactly what it asserts about itself.
    /// </remarks>
    [Fact]
    public async Task LocalBase_ProducesARunnableImageWithoutContactingARegistry()
    {
        ProjectFacts facts = await ProjectQuery.ReadAsync(SampleApiProject, CancellationToken.None);
        string baseImage = "mcr.microsoft.com/dotnet/aspnet:8.0";

        if (!await DockerIsAvailableAsync())
            return;

        if (!await ContainerImageBuilder.ImageExistsLocallyAsync(baseImage, CancellationToken.None))
            return;

        ContainerSourcePlan plan = new()
        {
            Kind = ContainerSourceKind.Project,
            Strategy = ContainerBuildStrategy.SdkContainerPublish,
            ProjectPath = facts.ProjectPath,
            Configuration = "Release",
            TargetFramework = "net8.0",
            RuntimeImage = baseImage,
            AssemblyFileName = $"{facts.AssemblyName}.dll",
        };

        ScopedLogger logger = CreateLogger();
        string repository = ContainerImageBuilder.ToRepositoryName("localbase-test");
        string tag = $"test-{Guid.NewGuid().ToString("N")[..8]}";
        string image = $"{repository}:{tag}";

        ContainerSourcePlan? built = await ContainerImageBuilder.BuildFromLocalBaseAsync(
            plan, "localbase-test", repository, tag, baseImage, logger, CancellationToken.None);

        try
        {
            Assert.NotNull(built);
            Assert.Equal(image, built.Image);

            // Shipping something other than what was planned is never silent.
            Assert.NotNull(built.FallbackReason);
            Assert.Contains(baseImage, built.FallbackReason, StringComparison.Ordinal);

            // The image has to carry the labels the leftover sweep finds it by, or a killed run
            // leaves it behind for good.
            // The format is quoted because the arguments travel as one string: an unquoted template
            // holding a space is split, and its tail is then read as a second image name.
            ContainerDockerCommands.CommandResult labels = await ContainerDockerCommands.RunAsync(
                $"image inspect {image} --format \"{{{{json .Config.Labels}}}}\"",
                CancellationToken.None);

            Assert.Equal(0, labels.ExitCode);
            Assert.Contains($"\"{ContainerLeftovers.BuildLabel}\":\"{ContainerLeftovers.BuildLabelValue}\"", labels.StandardOutput, StringComparison.Ordinal);
            Assert.Contains($"\"{ContainerLeftovers.VendorLabel}\":\"{ContainerLeftovers.VendorLabelValue}\"", labels.StandardOutput, StringComparison.Ordinal);

            // The entry point has to be the one the runtime can actually start.
            ContainerDockerCommands.CommandResult entryPoint = await ContainerDockerCommands.RunAsync(
                $"image inspect {image} --format \"{{{{json .Config.Entrypoint}}}}\"",
                CancellationToken.None);

            Assert.Contains($"{facts.AssemblyName}.dll", entryPoint.StandardOutput, StringComparison.Ordinal);

            // And the application has to be where the entry point says it is.
            ContainerDockerCommands.CommandResult listing = await ContainerDockerCommands.RunAsync(
                $"run --rm --entrypoint ls {image} /app/{facts.AssemblyName}.dll",
                CancellationToken.None);

            Assert.Equal(0, listing.ExitCode);

            // The generated files describe the build; they do not belong inside it.
            ContainerDockerCommands.CommandResult dockerfile = await ContainerDockerCommands.RunAsync(
                $"run --rm --entrypoint ls {image} /app/Dockerfile",
                CancellationToken.None);

            Assert.NotEqual(0, dockerfile.ExitCode);
        }
        finally
        {
            await ContainerImageBuilder.RemoveImageAsync(image, CancellationToken.None);
        }
    }

    /// <summary>
    /// A second environment wanting the same application gets the image the first one built.
    /// </summary>
    /// <remarks>
    /// This is the difference between a suite that takes minutes and one that takes tens of them: a
    /// dozen chapters against one unchanged application used to pay for a dozen identical builds.
    /// The second build here has to be near-instant, and it has to survive the first environment
    /// tearing itself down -- which removes the image it built, unless something knows better.
    /// </remarks>
    [Fact]
    public async Task ASecondEnvironment_ReusesTheImageRatherThanBuildingItAgain()
    {
        string baseImage = "mcr.microsoft.com/dotnet/aspnet:8.0";

        if (!await DockerIsAvailableAsync() || !await ContainerImageBuilder.ImageExistsLocallyAsync(baseImage, CancellationToken.None))
            return;

        ProjectFacts facts = await ProjectQuery.ReadAsync(SampleApiProject, CancellationToken.None);
        ContainerSource source = ContainerSource.Project(facts.ProjectPath).WithTargetFramework("net8.0").BuiltAsImage();
        ScopedLogger logger = CreateLogger();

        ContainerSourcePlan first = await ContainerImageBuilder.BuildAsync(
            await ContainerSourceResolver.PlanAsync(source, CancellationToken.None), "reuse-one", logger, CancellationToken.None);

        Assert.NotNull(first.Image);

        try
        {
            // Exactly what a component does when its environment goes away. It must not take an
            // image the rest of the run still wants with it.
            await ContainerImageBuilder.RemoveImageAsync(first.Image!, CancellationToken.None);
            Assert.True(
                await ContainerImageBuilder.ImageExistsLocallyAsync(first.Image!, CancellationToken.None),
                "Teardown removed an image that later environments in the same run still reuse.");

            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            ContainerSourcePlan second = await ContainerImageBuilder.BuildAsync(
                await ContainerSourceResolver.PlanAsync(source, CancellationToken.None), "reuse-two", logger, CancellationToken.None);
            stopwatch.Stop();

            Assert.Equal(first.Image, second.Image);

            // A rebuild is minutes. Anything in that range means the reuse did not happen.
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"The second environment took {stopwatch.Elapsed}, so it rebuilt.");
        }
        finally
        {
            ContainerImageBuilder.ForgetBuiltImages();
            await ContainerImageBuilder.RemoveImageAsync(first.Image!, CancellationToken.None);
        }
    }

    private static async Task<bool> DockerIsAvailableAsync()
    {
        try
        {
            ContainerDockerCommands.CommandResult result = await ContainerDockerCommands.RunAsync("--version", CancellationToken.None);
            return result.ExitCode == 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private static DotNetCliResult Failure(string standardOutput, string standardError)
        => new(["publish"], 1, standardOutput, standardError);

    private static string SampleApiProject
    {
        get
        {
            for (DirectoryInfo? current = new(Path.GetDirectoryName(typeof(UnreachableRegistryTests).Assembly.Location)!);
                 current is not null;
                 current = current.Parent)
            {
                if (current.EnumerateFiles("*.slnx").Any())
                    return Path.Combine(current.FullName, "UnitTests", "TestFramework.Container.Web.SampleApi", "TestFramework.Container.Web.SampleApi.csproj");
            }

            throw new InvalidOperationException("The repository root could not be located from the test assembly.");
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
