using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TestFramework.Container.Sources;
using TestFramework.Core.Environment;
using TestFramework.Core.Exceptions;
using TestFramework.Core.Timelines;
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

    private sealed class OrdersApiDefinition : DockerApiDefinition
    {
        public override ApiIdentifier Identifier => "orders";

        public override ContainerSource Source => ContainerSource.Directory(AppContext.BaseDirectory);

        protected override void Configure(DockerApiBuilder builder) => builder.UseSql<SalesSqlDefinition>("ConnectionStrings:Sales");
    }

    private sealed class StandaloneApiDefinition : DockerApiDefinition
    {
        public override ApiIdentifier Identifier => "orders";

        public override ContainerSource Source => ContainerSource.Directory(AppContext.BaseDirectory);

        protected override void Configure(DockerApiBuilder builder) => builder.WithHealthPath("/health");
    }

    private sealed class DuplicateOrdersApiDefinition : DockerApiDefinition
    {
        public override ApiIdentifier Identifier => "orders";

        public override ContainerSource Source => ContainerSource.Directory(AppContext.BaseDirectory);

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
    public void ResolveComponents_StartsTheApiForARequirementItDeclares()
    {
        DockerWebEnvironment environment = DockerWebEnvironment.For<StandaloneApiDefinition>();

        IReadOnlyCollection<EnvComponentIdentifier> resolved = environment.ResolveComponents([], [new EnvironmentRequirement(WebEnvironmentResourceKinds.RestApi, "orders")]);

        Assert.Contains(DockerWebEnvironment.ApiComponentId, resolved);
        Assert.Contains("web.restapi/orders", environment.Nodes.Select(node => node.ToString()));
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
    public async Task AnApiNeitherADefinitionNorConfigurationDeclares_IsRefusedBeforeAnythingStarts()
    {
        // The environment's own "no definition declares it" refusal is gone: its definitions are on the run's
        // resource list, and the engine refuses what nothing declares - before a single container starts.
        DockerWebEnvironment environment = DockerWebEnvironment.For<StandaloneApiDefinition>();
        Timeline timeline = Timeline.Create()
            .Trigger(WebExt.Api.Http("billing").Get("api/invoices").Call()).Name("bill")
            .Build();

        FrameworkConfigurationException refusal = await Assert.ThrowsAsync<FrameworkConfigurationException>(
            () => timeline.SetupRun().SetEnv(environment).RunAsync());

        Assert.Contains("Step 'bill' requires web.restapi 'billing'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(refusal.AvailableOptions, option => option.StartsWith("web.restapi 'orders'", StringComparison.Ordinal));
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
