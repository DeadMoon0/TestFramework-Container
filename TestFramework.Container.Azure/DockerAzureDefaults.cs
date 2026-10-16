using System;

namespace TestFramework.Container.Azure;

/// <summary>
/// The images, credentials and limits the Docker Azure environment uses when no definition overrides them.
/// </summary>
public static class DockerAzureDefaults
{
    /// <summary>
    /// The Functions host image used when the payload's framework cannot be worked out.
    /// </summary>
    /// <remarks>
    /// A fallback rather than a choice. The Functions host image carries exactly one .NET runtime, so
    /// an application built for a different one cannot start in it -- the worker exits with code 150
    /// and the only trace is inside the container log. Prefer
    /// <see cref="FunctionAppImageFor(string)"/>, which reads the tag off the payload.
    /// </remarks>
    public const string FunctionAppImage = "mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated8.0";

    /// <summary>
    /// The repository the isolated Functions host images come from.
    /// </summary>
    public const string FunctionAppImageRepository = "mcr.microsoft.com/azure-functions/dotnet-isolated";

    /// <summary>
    /// Returns the Functions host image that can run an application built for this framework.
    /// </summary>
    /// <param name="targetFramework">The framework the payload was built for, for example <c>net8.0</c>.</param>
    /// <returns>The image reference, or <see langword="null"/> when the framework is not one this can map.</returns>
    /// <remarks>
    /// The host image bundles one runtime and nothing else, so the tag has to follow the payload. It
    /// used to be pinned to 8.0 whatever the application targeted, which meant a Function App built
    /// for anything newer started, failed to launch its worker, and then simply never answered until
    /// the readiness timeout expired four minutes later.
    /// </remarks>
    public static string? FunctionAppImageFor(string? targetFramework)
    {
        if (string.IsNullOrWhiteSpace(targetFramework)
            || !targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string version = targetFramework[3..];

        // Only the shapes the isolated images are published under: 'netX.Y'. A framework identifier
        // such as net48 or netstandard2.0 is not something a Functions host image exists for.
        return version.Contains('.', StringComparison.Ordinal) && char.IsAsciiDigit(version[0])
            ? $"{FunctionAppImageRepository}:4-dotnet-isolated{version}"
            : null;
    }

    /// <summary>
    /// The connection string a definition's declared configuration carries until its container starts; the address
    /// the container publishes while it starts takes its place.
    /// </summary>
    public const string PlaceholderConnectionString = "placeholder://container-managed";

    /// <summary>
    /// The SQL Server image started for declared SQL databases and as the Service Bus emulator's backing store.
    /// </summary>
    public const string MsSqlImage = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";

    /// <summary>
    /// The SQL Server container's memory limit in megabytes.
    /// </summary>
    public const int MsSqlMemoryLimitMb = 1536;

    /// <summary>
    /// How long to wait for the started SQL Server to answer.
    /// </summary>
    /// <remarks>
    /// The same as the Web package allows for the same pinned image. Thirty seconds was enough on a warm
    /// machine and nowhere near enough on a first start or a loaded CI agent. Override it with
    /// <see cref="DockerAzureInfrastructureDefinition.MsSqlReadinessTimeout"/>.
    /// </remarks>
    public static readonly TimeSpan MsSqlReadinessTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// The Azurite image started for declared storage accounts.
    /// </summary>
    public const string AzuriteImage = "mcr.microsoft.com/azure-storage/azurite:3.33.0";

    /// <summary>
    /// Azurite's well-known development account name.
    /// </summary>
    public const string AzuriteAccountName = "devstoreaccount1";

    /// <summary>
    /// Azurite's well-known development account key.
    /// </summary>
    public const string AzuriteAccountKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    /// <summary>
    /// The Cosmos DB emulator image started for declared Cosmos containers.
    /// </summary>
    /// <remarks>
    /// Every other image here is pinned; this one cannot be. The Linux Cosmos emulator ships only
    /// moving tags — <c>vnext-preview</c> is the one Microsoft documents — so a pull can change the
    /// emulator between runs with nothing in the repository to show for it. Pin it with
    /// <see cref="DockerAzureInfrastructureDefinition.CosmosDbImage"/>, by tag or by digest, when a run
    /// has to be reproducible over time.
    /// </remarks>
    public const string CosmosDbImage = "mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview";

    /// <summary>
    /// How long the one-off bootstrap of a persistent container stack may take.
    /// </summary>
    /// <remarks>
    /// Generous because it is a ceiling, not a delay: nothing waits for it when startup is quick, and
    /// the cost of setting it too low is a green suite that fails for no reason. Two minutes was too
    /// low. A persistent slice can be a Docker network, Azurite, the Cosmos emulator, SQL Server, the
    /// Service Bus emulator - which only starts once SQL is serving - and one or more Function App
    /// hosts. That is minutes of legitimate work on a warm machine and more on a CI runner pulling
    /// images for the first time; measured at about two and a half minutes warm locally, and it
    /// overran two minutes on a hosted runner, where the timeout cancelled the token mid-start and
    /// surfaced as a container readiness failure rather than as the timeout it was. The one value for
    /// every road into a hosted stack: the fixture and <c>DockerAzureHostedEnvironment.StartAsync</c>
    /// once defaulted differently, and the lower default reproduced exactly that misdiagnosis.
    /// </remarks>
    public static readonly TimeSpan PersistentSetupTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The Cosmos DB emulator's well-known account key.
    /// </summary>
    public const string CosmosDbEmulatorAccountKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
    /// <summary>
    /// The Service Bus emulator image started for declared Service Bus entities.
    /// </summary>
    /// <remarks>
    /// This follows <c>latest</c>, which is what Microsoft's own compose templates and the
    /// Testcontainers module both use; the registry publishes no semantic version tag to pin to. A pull
    /// can therefore change the emulator between runs with nothing in the repository to show for it.
    /// Pin it with <see cref="DockerAzureInfrastructureDefinition.ServiceBusImage"/>, by tag or by
    /// digest, when a run has to be reproducible over time.
    /// </remarks>
    public const string ServiceBusImage = "mcr.microsoft.com/azure-messaging/servicebus-emulator:latest";
}
