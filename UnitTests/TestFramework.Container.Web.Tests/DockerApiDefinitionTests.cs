using System;
using System.Collections.Generic;
using System.Linq;
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

    private sealed class CompleteApiDefinition : DockerApiDefinition<SampleApiMarker>
    {
        public override ApiIdentifier Identifier => "orders";

        protected override void Configure(DockerApiBuilder builder) => builder
            .WithEnvironmentName("Testing")
            .WithHealthPath("health")
            .UseSql<SalesSqlDefinition>("ConnectionStrings:Sales")
            .WithSetting("Features:UseFakeClock", "true")
            .WithEnvironmentVariable("DOTNET_gcServer", "0");
    }

    private sealed class CollidingApiDefinition : DockerApiDefinition<SampleApiMarker>
    {
        public override ApiIdentifier Identifier => "orders";

        protected override void Configure(DockerApiBuilder builder) => builder
            .WithSetting("ConnectionStrings:Sales", "hand-written")
            .UseSql<SalesSqlDefinition>("ConnectionStrings:Sales");
    }

    private sealed class HealthlessApiDefinition : DockerApiDefinition<SampleApiMarker>
    {
        public override ApiIdentifier Identifier => "orders";

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
    public void EntryPointType_NamesTheApplicationAssembly()
        => Assert.Equal(typeof(SampleApiMarker), new CompleteApiDefinition().EntryPointType);

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

        Assert.Contains("more than one database", exception.Message, StringComparison.Ordinal);
    }

    internal sealed class OtherSqlDefinition : DockerSqlDefinition
    {
        public override SqlIdentifier Identifier => "other";

        protected override void Configure(DockerSqlBuilder builder) => builder.WithDatabase("OtherDb");
    }
}

/// <summary>
/// Covers the file the application actually reads its configuration from.
/// </summary>
public class ApiSettingsFileTests
{
    [Fact]
    public void Compose_NestsColonSeparatedPaths()
    {
        string json = ApiSettingsFile.Compose(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:Sales"] = "Data Source=sqlserver,1433",
            ["Features:UseFakeClock"] = "true",
            ["Logging:LogLevel:Default"] = "Debug",
        });

        Assert.Contains("\"ConnectionStrings\": {", json, StringComparison.Ordinal);
        Assert.Contains("\"Sales\": \"Data Source=sqlserver,1433\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Default\": \"Debug\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_IsStableSoTheFileReadsTheSameEveryRun()
    {
        Dictionary<string, string> first = new(StringComparer.OrdinalIgnoreCase) { ["B:One"] = "1", ["A:Two"] = "2" };
        Dictionary<string, string> second = new(StringComparer.OrdinalIgnoreCase) { ["A:Two"] = "2", ["B:One"] = "1" };

        Assert.Equal(ApiSettingsFile.Compose(first), ApiSettingsFile.Compose(second));
    }

    [Fact]
    public void Compose_FailsWhenOnePathIsNestedInsideAnother()
    {
        Dictionary<string, string> settings = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Features"] = "on",
            ["Features:UseFakeClock"] = "true",
        };

        FrameworkConfigurationException exception = Assert.Throws<FrameworkConfigurationException>(() => ApiSettingsFile.Compose(settings));

        Assert.Contains("Features", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FileName_FollowsTheHostingEnvironment()
        => Assert.Equal("appsettings.Testing.json", ApiSettingsFile.FileName("Testing"));
}
