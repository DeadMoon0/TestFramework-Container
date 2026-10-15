using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http;
using TestFramework.Azure.Configuration;
using TestFramework.Azure.Extensions;
using TestFramework.Config.Builder.InstanceBuilder;

namespace TestFramework.Container.Azure;

/// <summary>
/// Config builder and service collection extensions that point the Azure configuration at the containers this package starts.
/// </summary>
public static class DockerAzureConfigExtensions
{
    /// <summary>
    /// Loads the Azure configuration and configures Cosmos clients for the Docker Cosmos emulator.
    /// </summary>
    /// <remarks>Uses <c>null</c> for <c>provider</c>; a longer overload states it.</remarks>
    /// <param name="builder">The config instance builder.</param>
    /// <returns>The builder for fluent chaining.</returns>
    public static IConfigInstanceBuilder LoadDockerAzureConfig(this IConfigInstanceBuilder builder)
        => builder.LoadDockerAzureConfig(null);

    /// <summary>
    /// Loads the Azure configuration through <paramref name="provider"/> and applies
    /// <see cref="ConfigureDockerAzureCosmosEmulator"/> to the builder's services.
    /// </summary>
    /// <param name="builder">The config instance builder.</param>
    /// <param name="provider">Configuration provider passed on to the Azure configuration loader; <c>null</c> uses its default provider.</param>
    /// <returns>The builder for fluent chaining.</returns>
    public static IConfigInstanceBuilder LoadDockerAzureConfig(this IConfigInstanceBuilder builder, IConfigProvider? provider)
    {
        return builder
            .LoadAzureConfig(provider)
            .AddService(static services => services.ConfigureDockerAzureCosmosEmulator());
    }

    /// <summary>
    /// Configures Cosmos client options for the emulator: gateway mode, the emulator's development certificate accepted,
    /// and the client limited to the endpoint it was given.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection ConfigureDockerAzureCosmosEmulator(this IServiceCollection services)
    {
        services.ConfigureCosmosClientOptions(_ => CosmosEmulator.ClientOptions());

        return services;
    }
}