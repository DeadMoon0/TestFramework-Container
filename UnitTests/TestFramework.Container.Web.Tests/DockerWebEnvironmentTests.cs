using System;
using System.Collections.Generic;
using System.Reflection;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;
using TestFramework.Core.Exceptions;
using TestFramework.Core.Variables;
using TestFramework.Web;
using TestFramework.Web.Sql;
using TestFramework.Web.Sql.Artifacts;
using Xunit;

namespace TestFramework.Container.Web.Tests;

/// <summary>
/// Covers which components a run resolves, and the mistakes that are caught before Docker is touched.
/// </summary>
public class DockerWebEnvironmentTests
{
    internal sealed class Row
    {
        public int Id { get; set; }
    }

    private sealed class MainDefinition : DockerSqlDefinition
    {
        public override SqlIdentifier Identifier => "main";

        protected override void Configure(DockerSqlBuilder builder) => builder.WithDatabase("MainDb");
    }

    private sealed class ReportingDefinition : DockerSqlDefinition
    {
        public override SqlIdentifier Identifier => "reporting";

        protected override void Configure(DockerSqlBuilder builder) => builder.WithDatabase("ReportingDb");
    }

    private sealed class DuplicateMainDefinition : DockerSqlDefinition
    {
        public override SqlIdentifier Identifier => "main";

        protected override void Configure(DockerSqlBuilder builder) => builder.WithDatabase("OtherDb");
    }

    [Fact]
    public void ResolveComponents_StartsSqlServerForADeclaredDatabase()
    {
        DockerWebEnvironment environment = DockerWebEnvironment.For<MainDefinition>();

        IReadOnlyCollection<EnvComponentIdentifier> resolved = environment.ResolveComponents([], []);

        Assert.Contains(DockerWebEnvironment.SqlServerComponentId, resolved);
    }

    [Fact]
    public void ResolveComponents_StartsNothingWhenNothingIsDeclared()
    {
        DockerWebEnvironment environment = new();

        IReadOnlyCollection<EnvComponentIdentifier> resolved = environment.ResolveComponents([], []);

        Assert.Empty(resolved);
    }

    [Fact]
    public void ResolveComponents_RecordsTheIdentifierAStepRequires()
    {
        DockerWebEnvironment environment = DockerWebEnvironment.For<MainDefinition>();

        IReadOnlyCollection<EnvComponentIdentifier> resolved = environment.ResolveComponents([], [new EnvironmentRequirement(WebEnvironmentResourceKinds.Sql, "main")]);

        Assert.Contains(DockerWebEnvironment.SqlServerComponentId, resolved);
        Assert.Contains("main", environment.UsedSqlIdentifiers);
    }

    [Fact]
    public void ResolveComponents_RecordsTheIdentifierARowArtifactBelongsTo()
    {
        DockerWebEnvironment environment = DockerWebEnvironment.For<MainDefinition>();
        ArtifactInstanceGeneric artifact = CreateArtifactInstance<SqlRowArtifactDescriber<Row>, SqlRowArtifactData<Row>, SqlRowArtifactReference<Row>>(
            new SqlRowArtifactDescriber<Row>(),
            "row",
            new SqlRowArtifactReference<Row>("main", Var.Const("42")),
            new SqlRowArtifactData<Row>(new Row()));

        IReadOnlyCollection<EnvComponentIdentifier> resolved = environment.ResolveComponents([artifact], []);

        Assert.Contains(DockerWebEnvironment.SqlServerComponentId, resolved);
        Assert.Contains("main", environment.UsedSqlIdentifiers);
    }

    [Fact]
    public void ResolveComponents_ForgetsIdentifiersFromAPreviousResolution()
    {
        DockerWebEnvironment environment = DockerWebEnvironment.For<MainDefinition>().Include<ReportingDefinition>();

        environment.ResolveComponents([], [new EnvironmentRequirement(WebEnvironmentResourceKinds.Sql, "reporting")]);
        environment.ResolveComponents([], [new EnvironmentRequirement(WebEnvironmentResourceKinds.Sql, "main")]);

        Assert.Equal(["main"], environment.UsedSqlIdentifiers);
    }

    [Fact]
    public void ResolveComponents_FailsWhenARunUsesAnIdentifierNoDefinitionDeclares()
    {
        DockerWebEnvironment environment = DockerWebEnvironment.For<MainDefinition>();

        FrameworkConfigurationException exception = Assert.Throws<FrameworkConfigurationException>(
            () => environment.ResolveComponents([], [new EnvironmentRequirement(WebEnvironmentResourceKinds.Sql, "reporting")]));

        Assert.Contains("'reporting'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("main", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Include_FailsWhenTwoDefinitionsClaimTheSameIdentifier()
    {
        DockerWebEnvironment environment = DockerWebEnvironment.For<MainDefinition>();

        FrameworkConfigurationException exception = Assert.Throws<FrameworkConfigurationException>(() => environment.Include<DuplicateMainDefinition>());

        Assert.Contains("'main'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Include_AcceptsTheSameDefinitionTwice()
    {
        DockerWebEnvironment environment = DockerWebEnvironment.For<MainDefinition>().Include<MainDefinition>();

        Assert.Single(environment.GetSqlDefinitions());
    }

    [Fact]
    public void UseSql_OverridesTheServerSettingsTheContainerStartsWith()
    {
        DockerWebEnvironment environment = new DockerWebEnvironment()
            .UseSqlImage("mcr.microsoft.com/mssql/server:2019-latest")
            .UseSqlPassword("Another_Password1!")
            .UseSqlMemoryLimit(2048);

        Assert.Equal("mcr.microsoft.com/mssql/server:2019-latest", environment.SqlImage);
        Assert.Equal("Another_Password1!", environment.SqlPassword);
        Assert.Equal(2048, environment.SqlMemoryLimitMb);
    }

    [Fact]
    public void GetRequiredRuntimeState_FailsBeforeAComponentHasProducedIt()
        => Assert.Throws<FrameworkStateException>(() => new DockerWebEnvironment().GetRequiredRuntimeState<object>(DockerWebEnvironment.NetworkComponentId));

    // The artifact instance constructor is internal to the core package, so a test builds one the
    // same way the Azure environment tests do.
    private static ArtifactInstanceGeneric CreateArtifactInstance<TArtifactDescriber, TArtifactData, TArtifactReference>(
        TArtifactDescriber describer,
        ArtifactIdentifier identifier,
        TArtifactReference reference,
        TArtifactData data)
        where TArtifactDescriber : ArtifactDescriber<TArtifactDescriber, TArtifactData, TArtifactReference>, new()
        where TArtifactData : ArtifactData<TArtifactData, TArtifactDescriber, TArtifactReference>
        where TArtifactReference : ArtifactReference<TArtifactReference, TArtifactDescriber, TArtifactData>
    {
        return (ArtifactInstanceGeneric)Activator.CreateInstance(
            typeof(ArtifactInstance<TArtifactDescriber, TArtifactData, TArtifactReference>),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [describer, identifier, reference, data],
            culture: null)!;
    }
}
