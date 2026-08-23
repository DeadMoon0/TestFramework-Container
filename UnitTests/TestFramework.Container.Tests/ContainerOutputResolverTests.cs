using System;
using TestFramework.Core.Exceptions;
using Xunit;

namespace TestFramework.Container.Tests;

/// <summary>
/// Covers the one question left of the output-inference machinery: what framework an assembly was
/// built for. Everything a container ships is declared through a source now.
/// </summary>
public class ContainerOutputResolverTests
{
    [Fact]
    public void ResolveTargetFramework_ReturnsTheMonikerOfTheTestAssembly()
    {
        string moniker = ContainerOutputResolver.ResolveTargetFramework(typeof(ContainerOutputResolverTests).Assembly);

        Assert.StartsWith("net", moniker, StringComparison.Ordinal);
        Assert.Contains('.', moniker);
    }
}
