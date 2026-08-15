using System;
using System.Collections.Generic;
using System.Globalization;
using DotNet.Testcontainers.Networks;
using Testcontainers.MsSql;

namespace TestFramework.Container;

/// <summary>
/// Settings for a SQL Server container.
/// </summary>
/// <param name="Image">The image to run.</param>
/// <param name="Password">The <c>sa</c> password.</param>
/// <param name="MemoryLimitMb">The memory limit passed to the engine.</param>
/// <param name="NetworkAliases">Aliases other containers use to reach it.</param>
public sealed record MsSqlContainerOptions(string Image, string Password, int? MemoryLimitMb = null, IReadOnlyList<string>? NetworkAliases = null)
{
    /// <summary>
    /// The login the SQL Server images create.
    /// </summary>
    public const string UserName = "sa";
}

/// <summary>
/// Builds SQL Server containers with the settings both container packages need.
/// </summary>
/// <remarks>
/// Only container construction is shared. What the connection string is published as, and which
/// identifiers it applies to, differ per package and stay with the component that owns them.
/// </remarks>
public static class MsSqlContainerFactory
{
    /// <summary>
    /// Builds a SQL Server container. The caller starts it.
    /// </summary>
    /// <param name="options">The container settings.</param>
    /// <param name="network">The network to join, when the container must be reachable by other containers.</param>
    public static MsSqlContainer Create(MsSqlContainerOptions options, INetwork? network = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        MsSqlBuilder builder = new MsSqlBuilder(options.Image)
            .WithPassword(options.Password)
            .WithCreateParameterModifier(ContainerPortBinding.Apply);

        if (options.MemoryLimitMb is { } memoryLimit)
            builder = builder.WithEnvironment("MSSQL_MEMORY_LIMIT_MB", memoryLimit.ToString(CultureInfo.InvariantCulture));

        if (network is not null)
            builder = builder.WithNetwork(network);

        if (options.NetworkAliases is { Count: > 0 } aliases)
            builder = builder.WithNetworkAliases([.. aliases]);

        return builder.Build();
    }
}
