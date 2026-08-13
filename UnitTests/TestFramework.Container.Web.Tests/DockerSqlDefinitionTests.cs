using System;
using System.Linq;
using TestFramework.Core.Exceptions;
using TestFramework.Web.Sql;
using TestFramework.Web.Sql.Steps;
using Xunit;

namespace TestFramework.Container.Web.Tests;

/// <summary>
/// Covers what a database declaration accepts, and what it refuses before a container is started.
/// </summary>
public class DockerSqlDefinitionTests
{
    internal sealed class Order
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    internal sealed class Customer
    {
        public int Id { get; set; }
    }

    private sealed class CompleteDefinition : DockerSqlDefinition
    {
        public override SqlIdentifier Identifier => "main";

        protected override void Configure(DockerSqlBuilder builder) => builder
            .WithDatabase("SampleDb")
            .WithSchemaFromModels<Order, Customer>()
            .WithSchemaScript(SqlScript.FromText("CREATE VIEW v AS SELECT 1 AS x;", "views"))
            .WithResetMode(SqlResetMode.RecreateDatabase);
    }

    private sealed class NamelessDefinition : DockerSqlDefinition
    {
        public override SqlIdentifier Identifier => "main";

        protected override void Configure(DockerSqlBuilder builder) => builder.WithSchemaFromModels<Order>();
    }

    private sealed class InjectedNameDefinition : DockerSqlDefinition
    {
        public override SqlIdentifier Identifier => "main";

        protected override void Configure(DockerSqlBuilder builder) => builder.WithDatabase("Sample];DROP DATABASE [master");
    }

    private sealed class ResetWithoutScriptDefinition : DockerSqlDefinition
    {
        public override SqlIdentifier Identifier => "main";

        protected override void Configure(DockerSqlBuilder builder) => builder
            .WithDatabase("SampleDb")
            .WithResetMode(SqlResetMode.RunResetScript);
    }

    [Fact]
    public void Build_CarriesTheDatabaseModelsScriptsAndResetMode()
    {
        DockerSqlSpec spec = new CompleteDefinition().Build();

        Assert.Equal("SampleDb", spec.DatabaseName);
        Assert.Equal([typeof(Order), typeof(Customer)], spec.ModelTypes);
        Assert.Equal(["views"], spec.Scripts.Select(script => script.Description));
        Assert.Equal(SqlResetMode.RecreateDatabase, spec.ResetMode);
    }

    [Fact]
    public void Build_KeepsModelOrderAndIgnoresARepeatedModel()
    {
        DockerSqlBuilder builder = new(typeof(CompleteDefinition));
        builder.WithDatabase("SampleDb")
            .WithSchemaFromModels<Order>()
            .WithSchemaFromModels<Customer>()
            .WithSchemaFromModels<Order>();

        Assert.Equal([typeof(Order), typeof(Customer)], builder.Build().ModelTypes);
    }

    [Fact]
    public void Build_FailsWhenNoDatabaseIsNamed()
    {
        FrameworkConfigurationException exception = Assert.Throws<FrameworkConfigurationException>(() => new NamelessDefinition().Build());

        Assert.Contains("WithDatabase", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_FailsWhenTheDatabaseNameIsNotAPlainIdentifier()
    {
        FrameworkConfigurationException exception = Assert.Throws<FrameworkConfigurationException>(() => new InjectedNameDefinition().Build());

        Assert.Contains("plain identifier", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_FailsWhenAResetScriptIsSelectedButNotSupplied()
    {
        FrameworkConfigurationException exception = Assert.Throws<FrameworkConfigurationException>(() => new ResetWithoutScriptDefinition().Build());

        Assert.Contains("WithResetScript", exception.Message, StringComparison.Ordinal);
    }
}
