using System;
using System.IO;
using System.Runtime.CompilerServices;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Sources;

/// <summary>
/// How an application gets into its container.
/// </summary>
/// <remarks>
/// The source is declared rather than discovered. Everything a container needs -- which project,
/// which configuration, which framework, which image -- is either stated here or read from the
/// project by MSBuild, so nothing depends on where an assembly happened to be loaded from or on the
/// test project referencing the application at all.
/// </remarks>
public abstract class ContainerSource
{
    /// <summary>
    /// What kind of source this is, for the plan the environment reports.
    /// </summary>
    public abstract ContainerSourceKind Kind { get; }

    /// <summary>
    /// Runs an image that already exists.
    /// </summary>
    /// <param name="image">The image reference, for example <c>orders-api:ci-1234</c>.</param>
    /// <remarks>
    /// Nothing is built and nothing is discovered. This is the right source when a pipeline already
    /// produces the image the application ships as.
    /// </remarks>
    public static ContainerSource Image(string image) => new ImageContainerSource(image);

    /// <summary>
    /// Builds a project and runs the result.
    /// </summary>
    /// <param name="projectPath">
    /// The project file. A relative path is resolved against the source file that declares it, not
    /// against the working directory, so it reads the way it looks in the repository.
    /// </param>
    /// <param name="declaringFile">Filled in by the compiler; do not pass.</param>
    public static ProjectContainerSource Project(string projectPath, [CallerFilePath] string? declaringFile = null)
        => new(projectPath, declaringFile);

    /// <summary>
    /// Ships a directory that is already built.
    /// </summary>
    /// <param name="outputDirectory">The directory holding the built application.</param>
    /// <remarks>
    /// Nothing checks that the directory is current, so this hands responsibility for that to the
    /// caller. The plan reports when the assembly was last written.
    /// </remarks>
    public static ContainerSource Directory(string outputDirectory) => new DirectoryContainerSource(outputDirectory);

    /// <summary>
    /// Ships the build output of the assembly containing a type.
    /// </summary>
    /// <typeparam name="TEntryPoint">A type from the application assembly.</typeparam>
    /// <remarks>
    /// Kept for the definitions written before a source could be declared. It infers the project,
    /// the configuration and the output directory from the loaded assembly, which requires the test
    /// project to reference the application and can be wrong in ways that only show up as a
    /// container that fails to start. Prefer <see cref="Project"/>.
    /// </remarks>
    public static ContainerSource EntryPoint<TEntryPoint>()
        where TEntryPoint : class
        => new EntryPointContainerSource(typeof(TEntryPoint));

    /// <summary>
    /// Ships the build output of the assembly containing a type.
    /// </summary>
    /// <param name="entryPointType">A type from the application assembly.</param>
    public static ContainerSource EntryPoint(Type entryPointType) => new EntryPointContainerSource(entryPointType);
}

/// <summary>
/// The kinds of source an application container can have.
/// </summary>
public enum ContainerSourceKind
{
    /// <summary>An image that already exists.</summary>
    Image,

    /// <summary>A project the framework builds.</summary>
    Project,

    /// <summary>A directory that is already built.</summary>
    Directory,

    /// <summary>The build output behind a type, inferred from the loaded assembly.</summary>
    EntryPoint,
}

/// <summary>
/// Where a project is built.
/// </summary>
public enum ContainerBuildStrategy
{
    /// <summary>
    /// The SDK builds the image directly, with no Dockerfile. Needs a <c>docker</c> executable.
    /// </summary>
    SdkContainerPublish,

    /// <summary>
    /// The project is published to a temporary directory and copied into a runtime image.
    /// </summary>
    HostPublish,

    /// <summary>
    /// The project is built inside a container from a generated Dockerfile, leaving no build output
    /// on the host. Packages come from a feed generated out of the host's restore, so no credentials
    /// are needed inside the build.
    /// </summary>
    InContainer,
}

/// <summary>
/// An image that already exists.
/// </summary>
public sealed class ImageContainerSource : ContainerSource
{
    internal ImageContainerSource(string image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        ImageReference = image;
    }

    /// <inheritdoc />
    public override ContainerSourceKind Kind => ContainerSourceKind.Image;

    /// <summary>
    /// The image reference.
    /// </summary>
    public string ImageReference { get; }
}

/// <summary>
/// A directory that is already built.
/// </summary>
public sealed class DirectoryContainerSource : ContainerSource
{
    internal DirectoryContainerSource(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        OutputDirectory = Path.GetFullPath(outputDirectory);
    }

    /// <inheritdoc />
    public override ContainerSourceKind Kind => ContainerSourceKind.Directory;

    /// <summary>
    /// The directory holding the built application.
    /// </summary>
    public string OutputDirectory { get; }
}

/// <summary>
/// The build output behind a type.
/// </summary>
public sealed class EntryPointContainerSource : ContainerSource
{
    internal EntryPointContainerSource(Type entryPointType)
    {
        ArgumentNullException.ThrowIfNull(entryPointType);
        EntryPointType = entryPointType;
    }

    /// <inheritdoc />
    public override ContainerSourceKind Kind => ContainerSourceKind.EntryPoint;

    /// <summary>
    /// A type from the application assembly.
    /// </summary>
    public Type EntryPointType { get; }
}

/// <summary>
/// A project the framework builds.
/// </summary>
public sealed class ProjectContainerSource : ContainerSource
{
    internal ProjectContainerSource(string projectPath, string? declaringFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        ProjectPath = Path.IsPathRooted(projectPath)
            ? Path.GetFullPath(projectPath)
            : ResolveAgainstDeclaringFile(projectPath, declaringFile);

        DeclaringFile = declaringFile;
    }

    /// <inheritdoc />
    public override ContainerSourceKind Kind => ContainerSourceKind.Project;

    /// <summary>
    /// The absolute path of the project file.
    /// </summary>
    public string ProjectPath { get; }

    /// <summary>
    /// The source file that declared this source, when the compiler supplied it.
    /// </summary>
    public string? DeclaringFile { get; }

    /// <summary>
    /// Where the project is built. Defaults to letting the SDK build the image.
    /// </summary>
    public ContainerBuildStrategy Strategy { get; private set; } = ContainerBuildStrategy.SdkContainerPublish;

    /// <summary>
    /// The build configuration. Defaults to <c>Release</c>.
    /// </summary>
    public string Configuration { get; private set; } = "Release";

    /// <summary>
    /// The framework to build, when the project targets more than one.
    /// </summary>
    public string? TargetFramework { get; private set; }

    /// <summary>
    /// An explicit runtime base image.
    /// </summary>
    public string? RuntimeImage { get; private set; }

    /// <summary>
    /// An explicit SDK image, used when building inside a container.
    /// </summary>
    public string? SdkImage { get; private set; }

    /// <summary>
    /// An explicit build context, used when building inside a container.
    /// </summary>
    public string? ContextDirectory { get; private set; }

    /// <summary>
    /// Lets the SDK build the image directly. This is the default.
    /// </summary>
    public ProjectContainerSource BuiltAsImage() => WithStrategy(ContainerBuildStrategy.SdkContainerPublish);

    /// <summary>
    /// Publishes on the host and copies the output into a runtime image.
    /// </summary>
    public ProjectContainerSource BuiltOnHost() => WithStrategy(ContainerBuildStrategy.HostPublish);

    /// <summary>
    /// Builds inside a container, leaving no build output on the host.
    /// </summary>
    public ProjectContainerSource BuiltInContainer() => WithStrategy(ContainerBuildStrategy.InContainer);

    /// <summary>
    /// Sets the build configuration.
    /// </summary>
    /// <param name="configuration">The configuration name.</param>
    public ProjectContainerSource WithConfiguration(string configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        Configuration = configuration;
        return this;
    }

    /// <summary>
    /// Chooses which framework to build, for a project that targets several.
    /// </summary>
    /// <param name="targetFramework">The target framework moniker.</param>
    public ProjectContainerSource WithTargetFramework(string targetFramework)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFramework);
        TargetFramework = targetFramework;
        return this;
    }

    /// <summary>
    /// Overrides the runtime base image, which otherwise follows the project's framework and SDK.
    /// </summary>
    /// <param name="image">The image reference.</param>
    public ProjectContainerSource WithRuntimeImage(string image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        RuntimeImage = image;
        return this;
    }

    /// <summary>
    /// Overrides the SDK image used when building inside a container.
    /// </summary>
    /// <param name="image">The image reference.</param>
    public ProjectContainerSource WithSdkImage(string image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        SdkImage = image;
        return this;
    }

    /// <summary>
    /// Overrides the build context used when building inside a container.
    /// </summary>
    /// <param name="contextDirectory">The directory to send as the build context.</param>
    public ProjectContainerSource WithContext(string contextDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextDirectory);
        ContextDirectory = Path.GetFullPath(contextDirectory);
        return this;
    }

    private ProjectContainerSource WithStrategy(ContainerBuildStrategy strategy)
    {
        Strategy = strategy;
        return this;
    }

    private static string ResolveAgainstDeclaringFile(string projectPath, string? declaringFile)
    {
        if (string.IsNullOrWhiteSpace(declaringFile))
        {
            throw new FrameworkConfigurationException(
                $"The project path '{projectPath}' is relative and the declaring source file is unknown, so there is nothing to resolve it against.",
                ["Pass an absolute path to ContainerSource.Project(...)."]);
        }

        string declaringDirectory = Path.GetDirectoryName(declaringFile)
            ?? throw new FrameworkConfigurationException($"The declaring source file '{declaringFile}' has no directory.");

        return Path.GetFullPath(projectPath, declaringDirectory);
    }
}
