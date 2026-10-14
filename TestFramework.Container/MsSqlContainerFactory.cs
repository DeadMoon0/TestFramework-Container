using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using DotNet.Testcontainers.Networks;
using Testcontainers.MsSql;

namespace TestFramework.Container;

/// <summary>
/// Settings for a SQL Server container.
/// </summary>
public sealed record MsSqlContainerOptions
{
    /// <summary>
    /// The image to run.
    /// </summary>
    public required string Image { get; init; }

    /// <summary>
    /// The <c>sa</c> password.
    /// </summary>
    public required string Password { get; init; }

    /// <summary>
    /// The memory limit passed to the engine.
    /// </summary>
    public int? MemoryLimitMb { get; init; }

    /// <summary>
    /// Aliases other containers use to reach it.
    /// </summary>
    public IReadOnlyList<string>? NetworkAliases { get; init; }

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
    // Ambiguous glyphs are left out so a password that ends up in a log can be read back, and every
    // character that means something inside a connection string is left out so it never has to be
    // escaped.
    private const string PasswordAlphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>
    /// Creates a password for one environment's SQL Server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A constant password published in a package means every machine that runs the suite runs a
    /// server whose <c>sa</c> login is public knowledge. Generating one per environment costs nothing:
    /// the password only has to outlive the container, and everything that needs it is handed it.
    /// </para>
    /// <para>
    /// The shape satisfies SQL Server's complexity policy by construction — upper case, lower case, a
    /// digit and a symbol are all present regardless of what the random part draws.
    /// </para>
    /// </remarks>
    public static string CreateMsSqlPassword()
    {
        char[] characters = new char[24];
        for (int index = 0; index < characters.Length; index++)
            characters[index] = PasswordAlphabet[RandomNumberGenerator.GetInt32(PasswordAlphabet.Length)];

        return $"Tf{new string(characters)}1!";
    }

    /// <summary>
    /// Builds a SQL Server container. The caller starts it.
    /// </summary>
    /// <remarks>Uses <c>null</c> for <c>network</c>; a longer overload states it.</remarks>
    /// <param name="options">The container settings.</param>
    public static MsSqlContainer Create(MsSqlContainerOptions options)
        => Create(options, null);

    /// <summary>
    /// Builds a SQL Server container. The caller starts it.
    /// </summary>
    /// <param name="options">The container settings.</param>
    /// <param name="network">The network to join, when the container must be reachable by other containers.</param>
    public static MsSqlContainer Create(MsSqlContainerOptions options, INetwork? network)
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
