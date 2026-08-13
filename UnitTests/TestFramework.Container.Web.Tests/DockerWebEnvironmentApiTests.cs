using System;
using System.Collections.Generic;
using TestFramework.Container.Web.SampleApi;
using TestFramework.Core.Environment;
using TestFramework.Core.Exceptions;
using TestFramework.Web;
using TestFramework.Web.Identifier;
using TestFramework.Web.Sql;
using Xunit;

namespace TestFramework.Container.Web.Tests;

/// <summary>
/// Covers which components an application declaration resolves, and the bindings that are checked
/// before Docker is touched.
/// </summary>
public class DockerWebEnvironmentApiTests
{
    private sealed class SalesSqlDefinition : DockerSqlDefinition
    {
        public override SqlIdentifier Identifier => "sales";

        protected override void Configure(DockerSqlBuilder builder) => builder.WithDatabase("SalesDb");
    }

    private sealed class OrdersApiDefinition : DockerApiDefinition<SampleApiMarker>
    {
        public override ApiIdentifier Identifier => "orders";

        protected override void Configure(DockerApiBuilder builder) => builder.UseSql<SalesSqlDefinition>("ConnectionStrings:Sales");
    }

    private sealed class StandaloneApiDefinition : DockerApiDefinition<SampleApiMarker>
    {
        public override ApiIdentifier Identifier => "orders";

        protected override void Configure(DockerApiBuilder builder) => builder.WithHealthPath("/health");
    }

    private sealed class DuplicateOrdersApiDefinition : DockerApiDefinition<SampleApiMarker>
    {
        public override ApiIdentifier Identifier => "orders";

        protected override void Configure(DockerApiBuilder builder) => builder.WithoutHealthCheck();
    }

    [Fact]
    public void ResolveComponents_StartsTheApplicationForADeclaredApi()
    {
        DockerWebEnvironment environment = DockerWebEnvironment.For<StandaloneApiDefinition>();

        IReadOnlyCollection<EnvComponentIdentifier> resolved = environment.ResolveComponents([], []);

        Assert.Contains(DockerWebEnvironment.ApiComponentId, resolved);
        Assert.DoesNotContain(DockerWebEnvironment.SqlServerComponentId, resolved);
    }

    [Fact]
    public void ResolveComponents_RecordsTheIdentifierAnApiStepRequires()
    {
        DockerWebEnvironment environment = DockerWebEnvironment.For<StandaloneApiDefinition>();

        IReadOnlyCollection<EnvComponentIdentifier> resolved = environment.ResolveComponents([], [new EnvironmentRequirement(WebEnvironmentResourceKinds.RestApi, "orders")]);

        Assert.Contains(DockerWebEnvironment.ApiComponentId, resolved);
        Assert.Contains("orders", environment.UsedApiIdentifiers);
    }

    [Fact]
    public void ResolveComponents_StartsBothWhenTheApplicationNeedsADatabase()
    {
        DockerWebEnvironment environment = DockerWebEnvironment.For<OrdersApiDefinition>().Include<SalesSqlDefinition>();

        IReadOnlyCollection<EnvComponentIdentifier> resolved = environment.ResolveComponents([], []);

        Assert.Contains(DockerWebEnvironment.ApiComponentId, resolved);
        Assert.Contains(DockerWebEnvironment.SqlServerComponentId, resolved);
    }

    [Fact]
    public void ResolveComponents_FailsWhenAnApplicationBindsToAnUndeclaredDatabase()
    {
        DockerWebEnvironment environment = DockerWebEnvironment.For<OrdersApiDefinition>();

        FrameworkConfigurationException exception = Assert.Throws<FrameworkConfigurationException>(() => environment.ResolveComponents([], []));

        Assert.Contains("'sales'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("OrdersApiDefinition", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveComponents_FailsWhenARunUsesAnUndeclaredApiIdentifier()
    {
        DockerWebEnvironment environment = DockerWebEnvironment.For<StandaloneApiDefinition>();

        FrameworkConfigurationException exception = Assert.Throws<FrameworkConfigurationException>(
            () => environment.ResolveComponents([], [new EnvironmentRequirement(WebEnvironmentResourceKinds.RestApi, "billing")]));

        Assert.Contains("'billing'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("orders", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Include_FailsWhenTwoDefinitionsClaimTheSameApiIdentifier()
    {
        DockerWebEnvironment environment = DockerWebEnvironment.For<StandaloneApiDefinition>();

        FrameworkConfigurationException exception = Assert.Throws<FrameworkConfigurationException>(() => environment.Include<DuplicateOrdersApiDefinition>());

        Assert.Contains("'orders'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetApiDefinitions_OrdersByIdentifierSoStartupIsPredictable()
    {
        DockerWebEnvironment environment = DockerWebEnvironment.For<StandaloneApiDefinition>();

        Assert.Equal(["orders"], [.. System.Linq.Enumerable.Select(environment.GetApiDefinitions(), definition => definition.Identifier.Identifier)]);
    }
}
