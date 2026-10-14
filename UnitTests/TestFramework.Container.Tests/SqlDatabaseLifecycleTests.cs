using System;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Core.Exceptions;
using Xunit;

namespace TestFramework.Container.Tests;

/// <summary>
/// A recreate drops the test's own database and never one the server or the Service Bus emulator lives in.
/// </summary>
public class SqlDatabaseLifecycleTests
{
    [Theory]
    [InlineData("master")]
    [InlineData("model")]
    [InlineData("msdb")]
    [InlineData("tempdb")]
    [InlineData("MASTER")]
    public void TheServersOwnDatabases_AreProtected(string name)
        => Assert.True(SqlDatabaseLifecycle.IsProtected(name));

    [Theory]
    [InlineData("SbGatewayDatabase")]
    [InlineData("SbMessageContainerDatabase00001")]
    [InlineData("SbMessageContainerDatabase00002")]
    public void TheServiceBusEmulatorsDatabases_AreProtected(string name)
    {
        // Measured against the emulator image. The old rule matched "SbEmulator*" and "*ServiceBus*", neither of
        // which these are, so a purge could drop the emulator's state mid-suite.
        Assert.True(SqlDatabaseLifecycle.IsProtected(name));
    }

    [Theory]
    [InlineData("OrdersDb")]
    [InlineData("ServiceBusOrders")]
    [InlineData("SbMessageContainerDatabase")]
    [InlineData("SbGatewayDatabaseCopy")]
    public void ATestsOwnDatabase_IsNotProtected_WhateverItIsCalled(string name)
        => Assert.False(SqlDatabaseLifecycle.IsProtected(name));

    [Fact]
    public async Task RecreatingAProtectedDatabase_IsRefusedBeforeAnythingIsSent()
    {
        // No server at this address: the refusal has to come before any connection is tried.
        FrameworkConfigurationException refusal = await Assert.ThrowsAsync<FrameworkConfigurationException>(
            () => SqlDatabaseLifecycle.RecreateAsync("Server=127.0.0.1,1;Connect Timeout=1", "SbGatewayDatabase", CancellationToken.None));

        Assert.Contains("'SbGatewayDatabase' is not dropped", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Orders", "[Orders]")]
    [InlineData("odd]name", "[odd]]name]")]
    public void ANameIsQuotedTheWaySqlServerQuotesIt(string name, string quoted)
        => Assert.Equal(quoted, SqlDatabaseLifecycle.QuoteName(name));
}
