using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Web.Sites;

/// <summary>
/// Where a site's payload comes from.
/// </summary>
/// <remarks>
/// A site is not built into an image of its own: a fixed serving image gets the payload copied in,
/// and the source only says where that payload comes from. The source is declared rather than
/// discovered -- which directory, which build script, which build image -- so nothing depends on
/// what happens to lie around on the test host.
/// </remarks>
public abstract class SiteSource
{
    /// <summary>
    /// What kind of source this is, for the plan the environment reports.
    /// </summary>
    public abstract SiteSourceKind Kind { get; }

    /// <summary>
    /// Runs an image that already exists, as-is.
    /// </summary>
    /// <param name="image">The image reference, for example <c>shop-ui:ci-1234</c>.</param>
    /// <remarks>
    /// The image brings its own web server and its own server configuration, so proxy routes and the
    /// SPA fallback cannot be declared for it. Generated configuration files can still be copied in.
    /// </remarks>
    public static SiteSource Image(string image) => new ImageSiteSource(image);

    /// <summary>
    /// Ships a directory that is already built.
    /// </summary>
    /// <param name="distDirectory">The directory holding the built site, with its <c>index.html</c> at the root.</param>
    /// <remarks>
    /// Nothing checks that the directory is current, so this hands responsibility for that to the
    /// caller. The plan reports when the index was last written.
    /// </remarks>
    public static SiteSource Directory(string distDirectory) => new DirectorySiteSource(distDirectory);

    /// <summary>
    /// Builds an npm project and ships its output. Covers every npm-based framework: Angular, React,
    /// Vue, Svelte, Vite and plain bundlers alike.
    /// </summary>
    /// <param name="projectDirectory">
    /// The directory holding <c>package.json</c>. A relative path is resolved against the source file
    /// that declares it, not against the working directory, so it reads the way it looks in the
    /// repository.
    /// </param>
    /// <param name="declaringFile">Filled in by the compiler; do not pass.</param>
    public static NpmProjectSiteSource NpmProject(string projectDirectory, [CallerFilePath] string? declaringFile = null)
        => new(projectDirectory, declaringFile);

    /// <summary>
    /// Builds with any toolchain by running a declared command in a declared image, and ships the
    /// output. The build environment is the image, so the test host needs only Docker.
    /// </summary>
    /// <param name="projectDirectory">
    /// The directory holding the sources. A relative path is resolved against the source file that
    /// declares it.
    /// </param>
    /// <param name="buildImage">The image the command runs in, for example <c>hugomods/hugo</c>.</param>
    /// <param name="buildCommand">The command and its arguments, one element each.</param>
    /// <param name="declaringFile">Filled in by the compiler; do not pass.</param>
    public static ContainerBuildSiteSource ContainerBuild(
        string projectDirectory,
        string buildImage,
        string[] buildCommand,
        [CallerFilePath] string? declaringFile = null)
        => new(projectDirectory, buildImage, buildCommand, declaringFile);

    internal static string ResolveDirectory(string path, string? declaringFile, string whatItIs)
    {
        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);

        if (string.IsNullOrWhiteSpace(declaringFile))
        {
            throw new FrameworkConfigurationException(
                $"The {whatItIs} '{path}' is relative and the declaring source file is unknown, so there is nothing to resolve it against.",
                ["Pass an absolute path."]);
        }

        string declaringDirectory = Path.GetDirectoryName(declaringFile)
            ?? throw new FrameworkConfigurationException($"The declaring source file '{declaringFile}' has no directory.");

        return Path.GetFullPath(path, declaringDirectory);
    }
}

/// <summary>
/// The kinds of source a site payload can have.
/// </summary>
public enum SiteSourceKind
{
    /// <summary>An image that already exists.</summary>
    Image,

    /// <summary>A directory that is already built.</summary>
    Directory,

    /// <summary>An npm project the environment builds.</summary>
    NpmProject,

    /// <summary>Sources built by a declared command in a declared image.</summary>
    ContainerBuild,
}

/// <summary>
/// Where a site build runs.
/// </summary>
public enum SiteBuildStrategy
{
    /// <summary>The build runs on the test host, which needs the toolchain installed.</summary>
    HostBuild,

    /// <summary>The build runs inside a container, so the test host needs only Docker.</summary>
    InContainerBuild,
}

/// <summary>
/// An image that already exists.
/// </summary>
public sealed class ImageSiteSource : SiteSource
{
    internal ImageSiteSource(string image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        ImageReference = image;
    }

    /// <inheritdoc />
    public override SiteSourceKind Kind => SiteSourceKind.Image;

    /// <summary>
    /// The image reference.
    /// </summary>
    public string ImageReference { get; }
}

/// <summary>
/// A directory that is already built.
/// </summary>
public sealed class DirectorySiteSource : SiteSource
{
    internal DirectorySiteSource(string distDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distDirectory);
        DistDirectory = Path.GetFullPath(distDirectory);
    }

    /// <inheritdoc />
    public override SiteSourceKind Kind => SiteSourceKind.Directory;

    /// <summary>
    /// The directory holding the built site.
    /// </summary>
    public string DistDirectory { get; }
}

/// <summary>
/// An npm project the environment builds.
/// </summary>
public sealed class NpmProjectSiteSource : SiteSource
{
    // The script name is the only free-form text that reaches the npm command line. Restricting it
    // keeps every invocation safely quotable, on Windows in particular, where npm is a cmd file with
    // its own argument parsing rules.
    private static readonly Regex SafeScriptName = new("^[A-Za-z0-9:_.-]+$", RegexOptions.None, TimeSpan.FromSeconds(1));

    internal NpmProjectSiteSource(string projectDirectory, string? declaringFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ProjectDirectory = ResolveDirectory(projectDirectory, declaringFile, "project directory");
    }

    /// <inheritdoc />
    public override SiteSourceKind Kind => SiteSourceKind.NpmProject;

    /// <summary>
    /// The directory holding <c>package.json</c>.
    /// </summary>
    public string ProjectDirectory { get; }

    /// <summary>
    /// Where the build runs. Defaults to the test host.
    /// </summary>
    public SiteBuildStrategy Strategy { get; private set; } = SiteBuildStrategy.HostBuild;

    /// <summary>
    /// The image the build runs in when it runs inside a container.
    /// </summary>
    public string BuildImage { get; private set; } = DockerWebDefaults.SiteNodeImage;

    /// <summary>
    /// The npm script the build runs. Defaults to <c>build</c>.
    /// </summary>
    public string BuildScript { get; private set; } = "build";

    /// <summary>
    /// The output directory relative to the project, when declared. Probed by convention otherwise.
    /// </summary>
    public string? DistPath { get; private set; }

    /// <summary>
    /// Whether <c>npm ci</c> runs first when <c>node_modules</c> is absent. Host builds only; a
    /// container build always installs, because the host's <c>node_modules</c> never travels into it.
    /// </summary>
    public bool InstallWhenMissing { get; private set; } = true;

    /// <summary>
    /// How long the build may run before it is killed.
    /// </summary>
    public TimeSpan BuildTimeout { get; private set; } = DockerWebDefaults.SiteBuildTimeout;

    /// <summary>
    /// Runs the build on the test host, which needs node and npm installed. This is the default.
    /// </summary>
    public NpmProjectSiteSource BuiltOnHost()
    {
        Strategy = SiteBuildStrategy.HostBuild;
        return this;
    }

    /// <summary>
    /// Runs the build inside a node container, so the test host needs only Docker.
    /// </summary>
    /// <param name="nodeImage">The node image to build in, or <see langword="null"/> for the default.</param>
    public NpmProjectSiteSource BuiltInContainer(string? nodeImage = null)
    {
        if (nodeImage is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(nodeImage);

        Strategy = SiteBuildStrategy.InContainerBuild;
        BuildImage = nodeImage ?? DockerWebDefaults.SiteNodeImage;
        return this;
    }

    /// <summary>
    /// Overrides the npm script the build runs.
    /// </summary>
    /// <param name="script">The script name, as it appears under <c>scripts</c> in <c>package.json</c>.</param>
    public NpmProjectSiteSource WithBuildScript(string script)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        if (!SafeScriptName.IsMatch(script))
            throw new FrameworkConfigurationException($"The npm script name '{script}' contains characters outside 'A-Z a-z 0-9 : _ . -', which cannot be passed to npm safely on every host.");

        BuildScript = script;
        return this;
    }

    /// <summary>
    /// Declares the output directory, relative to the project directory.
    /// </summary>
    /// <param name="relativePath">The dist path, for example <c>dist/shop/browser</c>.</param>
    public NpmProjectSiteSource WithDistPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        DistPath = relativePath;
        return this;
    }

    /// <summary>
    /// Skips <c>npm ci</c> even when <c>node_modules</c> is absent, for a host that manages its own install.
    /// </summary>
    public NpmProjectSiteSource WithoutInstall()
    {
        InstallWhenMissing = false;
        return this;
    }

    /// <summary>
    /// Overrides how long the build may run.
    /// </summary>
    /// <param name="timeout">The build timeout.</param>
    public NpmProjectSiteSource WithBuildTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        BuildTimeout = timeout;
        return this;
    }
}

/// <summary>
/// Sources built by a declared command in a declared image.
/// </summary>
public sealed class ContainerBuildSiteSource : SiteSource
{
    internal ContainerBuildSiteSource(string projectDirectory, string buildImage, string[] buildCommand, string? declaringFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildImage);
        ArgumentNullException.ThrowIfNull(buildCommand);
        if (buildCommand.Length == 0)
            throw new FrameworkConfigurationException("A container build declares no command, so there is nothing to run in the build image.");

        ProjectDirectory = ResolveDirectory(projectDirectory, declaringFile, "project directory");
        BuildImage = buildImage;
        BuildCommand = [.. buildCommand];
    }

    /// <inheritdoc />
    public override SiteSourceKind Kind => SiteSourceKind.ContainerBuild;

    /// <summary>
    /// The directory holding the sources.
    /// </summary>
    public string ProjectDirectory { get; }

    /// <summary>
    /// The image the command runs in.
    /// </summary>
    public string BuildImage { get; }

    /// <summary>
    /// The command and its arguments.
    /// </summary>
    public IReadOnlyList<string> BuildCommand { get; }

    /// <summary>
    /// The output directory relative to the project. Required: an arbitrary toolchain has no
    /// conventions to probe.
    /// </summary>
    public string? DistPath { get; private set; }

    /// <summary>
    /// How long the build may run before it is killed.
    /// </summary>
    public TimeSpan BuildTimeout { get; private set; } = DockerWebDefaults.SiteBuildTimeout;

    /// <summary>
    /// Declares the output directory, relative to the project directory.
    /// </summary>
    /// <param name="relativePath">The dist path, for example <c>public</c>.</param>
    public ContainerBuildSiteSource WithDistPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        DistPath = relativePath;
        return this;
    }

    /// <summary>
    /// Overrides how long the build may run.
    /// </summary>
    /// <param name="timeout">The build timeout.</param>
    public ContainerBuildSiteSource WithBuildTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        BuildTimeout = timeout;
        return this;
    }
}
