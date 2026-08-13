using System;
using Xunit;

namespace TestFramework.Container.Tests;

/// <summary>
/// Covers the two addresses a container has, which is the distinction most easily got wrong.
/// </summary>
public class ContainerEndpointsTests
{
    [Fact]
    public void NetworkEndpoint_UsesTheAliasAndTheInternalPort()
    {
        Uri endpoint = ContainerEndpoints.NetworkEndpoint("orders-api", 8080);

        Assert.Equal("http://orders-api:8080/", endpoint.AbsoluteUri);
    }

    [Fact]
    public void NetworkEndpoint_AppendsAPathWhenGiven()
    {
        Uri endpoint = ContainerEndpoints.NetworkEndpoint("azurite", 10000, "http", "/devstoreaccount1");

        Assert.Equal("http://azurite:10000/devstoreaccount1", endpoint.AbsoluteUri);
    }

    [Fact]
    public void BuildEndpoint_HonoursTheScheme()
    {
        Uri endpoint = ContainerEndpoints.BuildEndpoint("cosmos-emulator", 8081, "https");

        Assert.Equal("https://cosmos-emulator:8081/", endpoint.AbsoluteUri);
    }

    [Fact]
    public void NetworkSqlConnectionString_TargetsTheAliasAndTheInternalPort()
    {
        string connectionString = ContainerEndpoints.NetworkSqlConnectionString("sql-server", "SampleDb", "sa", "secret");

        Assert.Contains("Data Source=sql-server,1433", connectionString, StringComparison.Ordinal);
        Assert.Contains("Initial Catalog=SampleDb", connectionString, StringComparison.Ordinal);
        Assert.Contains("Trust Server Certificate=True", connectionString, StringComparison.Ordinal);
    }

    [Fact]
    public void NetworkSqlConnectionString_DiffersFromWhatTheTestProcessWouldUse()
    {
        // The same database has two addresses at the same time. Publishing the wrong one is the
        // classic container-lane defect, so they must never be interchangeable.
        string forContainer = ContainerEndpoints.NetworkSqlConnectionString("sql-server", "SampleDb", "sa", "secret");

        Assert.DoesNotContain("localhost", forContainer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("127.0.0.1", forContainer, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NetworkEndpoint_RejectsABlankAlias(string alias)
        => Assert.Throws<ArgumentException>(() => ContainerEndpoints.NetworkEndpoint(alias, 8080));
}
