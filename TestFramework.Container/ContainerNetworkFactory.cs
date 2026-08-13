using System;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;

namespace TestFramework.Container;

/// <summary>
/// Creates the Docker network the components of one environment share.
/// </summary>
/// <remarks>
/// Every environment gets its own network with a unique name, so parallel runs and leftovers from a
/// crashed run cannot reach each other.
/// </remarks>
public static class ContainerNetworkFactory
{
    /// <summary>
    /// Creates and starts a uniquely named network.
    /// </summary>
    /// <param name="namePrefix">A prefix that makes the network recognizable in <c>docker network ls</c>.</param>
    /// <param name="cancellationToken">The cancellation token for the running setup.</param>
    public static async Task<INetwork> CreateAsync(string namePrefix, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namePrefix);

        INetwork network = new NetworkBuilder()
            .WithName($"{namePrefix}-{Guid.NewGuid():N}")
            .Build();

        await network.CreateAsync(cancellationToken).ConfigureAwait(false);
        return network;
    }
}
