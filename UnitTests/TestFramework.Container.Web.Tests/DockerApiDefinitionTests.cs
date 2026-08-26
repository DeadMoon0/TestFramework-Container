using System;
using System.Collections.Generic;
using System.Linq;
using TestFramework.Container.Sources;
using TestFramework.Container.Web.SampleApi;
using TestFramework.Core.Exceptions;
using TestFramework.Web.Identifier;
using TestFramework.Web.Sql;
using Xunit;

namespace TestFramework.Container.Web.Tests;

/// <summary>
/// Covers how an application declaration is read, and what it refuses before a container is started.
/// </summary>
public class DockerApiDefinitionTests
{
    internal sealed class SalesSqlDefinition : DockerSqlDefinition
    {
        public override SqlIdentifier Identifier => "sales";

        protected override void Configure(DockerSqlBuilder builder) => builder.WithDatabase("SalesDb");
    }

    private sealed class CompleteApiDefinition : DockerApiDefinition
    {
        public override ApiIdentifier Identifier => "orders";

        public override ContainerSource Source => ContainerSource.Directory(AppContext.BaseDirectory);

        protected override void Configure(DockerApiBuilder builder) => builder
            .WithEnvironmentName("Testing")
            .WithHealthPath("health")
            .UseSql<SalesSqlDefinition>("ConnectionStrings:Sales")
            .WithSetting("Features:UseFakeClock", "true")
            .WithEnvironmentVariable("DOTNET_gcServer", "0");
    }

    private sealed class CollidingApiDefinition : DockerApiDefinition
    {
        public override ApiIdentifier Identifier => "orders";

        public override ContainerSource Source => ContainerSource.Directory(AppContext.BaseDirectory);

        protected override void Configure(DockerApiBuilder builder) => builder
            .WithSetting("ConnectionStrings:Sales", "hand-written")
            .UseSql<SalesSqlDefinition>("ConnectionStrings:Sales");
    }

    private sealed class HealthlessApiDefinition : DockerApiDefinition
    {
        public override ApiIdentifier Identifier => "orders";

        public override ContainerSource Source => ContainerSource.Directory(AppContext.BaseDirectory);

        protected override void Configure(DockerApiBuilder builder) => builder.WithoutHealthCheck();
    }

    [Fact]
    public void Build_CarriesTheSettingsBindingsAndEnvironment()
    {
        DockerApiSpec spec = new CompleteApiDefinition().Build();

        Assert.Equal("Testing", spec.EnvironmentName);
        Assert.Equal("/health", spec.HealthPath);
        Assert.Equal(8080, spec.InternalPort);
        Assert.Equal("true", spec.Settings["Features:UseFakeClock"]);
        Assert.Equal("0", spec.EnvironmentVariables["DOTNET_gcServer"]);
        Assert.Equal(["sales"], spec.SqlBindings.Select(binding => binding.SqlIdentifier.Identifier));
        Assert.Equal(["ConnectionStrings:Sales"], spec.SqlBindings.Select(binding => binding.SettingPath));
    }

    [Fact]
    public void Source_CanBeADeclaredProjectInsteadOfAMarkerType()
    {
        ContainerSource source = new ProjectSourcedApiDefinition().Source;

        Assert.Equal(ContainerSourceKind.Project, source.Kind);
        Assert.EndsWith("TestFramework.Container.Web.SampleApi.csproj", Assert.IsType<ProjectContainerSource>(source).ProjectPath, StringComparison.Ordinal);
    }

    private sealed class ProjectSourcedApiDefinition : DockerApiDefinition
    {
        public override ApiIdentifier Identifier => "orders";

        // No marker type, and no reference from this project to the application.
        public override ContainerSource Source =>
            ContainerSource.Project("../TestFramework.Container.Web.SampleApi/TestFramework.Container.Web.SampleApi.csproj")
                .WithTargetFramework("net8.0");

        protected override void Configure(DockerApiBuilder builder) => builder.WithHealthPath("/health");
    }

    [Fact]
    public void WithoutHealthCheck_LeavesNoPathToProbe()
        => Assert.Null(new HealthlessApiDefinition().Build().HealthPath);

    [Fact]
    public void Build_FailsWhenASettingIsBothWrittenByHandAndBoundToADatabase()
    {
        FrameworkConfigurationException exception = Assert.Throws<FrameworkConfigurationException>(() => new CollidingApiDefinition().Build());

        Assert.Contains("ConnectionStrings:Sales", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveImage_FollowsTheFrameworkTheApplicationWasBuiltFor()
    {
        DockerApiSpec spec = new CompleteApiDefinition().Build();

        Assert.Equal("mcr.microsoft.com/dotnet/aspnet:8.0", spec.ResolveImage("net8.0"));
        Assert.Equal("mcr.microsoft.com/dotnet/aspnet:10.0", spec.ResolveImage("net10.0"));
    }

    [Fact]
    public void ResolveImage_PrefersAnExplicitImage()
    {
        DockerApiSpec spec = new DockerApiBuilder(typeof(CompleteApiDefinition)).WithImage("my-registry/api:1.2").Build();

        Assert.Equal("my-registry/api:1.2", spec.ResolveImage("net10.0"));
    }

    [Fact]
    public void ResolveImage_FailsOnAFrameworkItCannotMap()
    {
        DockerApiSpec spec = new CompleteApiDefinition().Build();

        FrameworkConfigurationException exception = Assert.Throws<FrameworkConfigurationException>(() => spec.ResolveImage("netstandard2.0"));

        Assert.Contains("WithImage", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_FailsWhenTwoDatabasesTargetTheSameSetting()
    {
        DockerApiBuilder builder = new DockerApiBuilder(typeof(CompleteApiDefinition))
            .UseSql<SalesSqlDefinition>("ConnectionStrings:Sales")
            .UseSql<OtherSqlDefinition>("ConnectionStrings:Sales");

        FrameworkConfigurationException exception = Assert.Throws<FrameworkConfigurationException>(builder.Build);

        Assert.Contains("more than one resource", exception.Message, StringComparison.Ordinal);
    }

    internal sealed class OtherSqlDefinition : DockerSqlDefinition
    {
        public override SqlIdentifier Identifier => "other";

        protected override void Configure(DockerSqlBuilder builder) => builder.WithDatabase("OtherDb");
    }
}

/// <summary>
/// The one thing about an API's settings file that is this package's own: which file the host loads.
/// </summary>
/// <remarks>
/// Composing the file was a copy of Core's <c>JsonPathDocument</c> - an API's settings, a site's
/// configuration file and a function app's settings are the same problem three times - so the copy is
/// gone and Core's suite covers the composing. What is left is the name, which only this package knows.
///
/// Deleting the copy did surface one real difference: this composer refused two paths where one nested
/// inside the other, and Core's silently let the deeper one win. Core refuses it now too, so nothing was
/// traded away for the deduplication.
/// </remarks>
public class ApiSettingsFileTests
{
    [Fact]
    public void FileName_IsTheOneTheHostingEnvironmentLoads()
    {
        Assert.Equal("appsettings.Testing.json", ApiSettingsFile.FileName("Testing"));
        Assert.Equal("appsettings.Staging.json", ApiSettingsFile.FileName("Staging"));
    }

    [Fact]
    public void FileName_RefusesAnEnvironmentWithNoName()
        => Assert.ThrowsAny<ArgumentException>(() => ApiSettingsFile.FileName(" "));
}
