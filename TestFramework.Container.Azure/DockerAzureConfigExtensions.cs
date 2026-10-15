using Microsoft.Extensions.DependencyInjection;
using TestFramework.Azure.Configuration;
using TestFramework.Azure.Extensions;
using TestFramework.Config.Builder.InstanceBuilder;

namespace TestFramework.Container.Azure;

/// <summary>
/// Turns this package on: the Azure configuration, with Cosmos clients pointed at the emulator this package starts.
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
    /// Loads the Azure configuration through <paramref name="provider"/> and configures Cosmos clients for the
    /// Docker Cosmos emulator.
    /// </summary>
    /// <remarks>
    /// The same registration as <see cref="AddDockerAzure(IServiceCollection, IConfigProvider?)"/>, made on the
    /// builder's services.
    /// </remarks>
    /// <param name="builder">The config instance builder.</param>
    /// <param name="provider">Configuration provider passed on to the Azure configuration loader; <c>null</c> uses its default provider.</param>
    /// <returns>The builder for fluent chaining.</returns>
    public static IConfigInstanceBuilder LoadDockerAzureConfig(this IConfigInstanceBuilder builder, IConfigProvider? provider)
        => builder.AddService(services => services.AddDockerAzure(provider));

    /// <summary>
    /// Registers the Azure configuration and configures Cosmos clients for the Docker Cosmos emulator, on a service
    /// collection built without a config instance.
    /// </summary>
    /// <remarks>Uses <c>null</c> for <c>provider</c>; a longer overload states it.</remarks>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddDockerAzure(this IServiceCollection services)
        => services.AddDockerAzure(null);

    /// <summary>
    /// Registers the Azure configuration through <paramref name="provider"/> and configures Cosmos clients for the
    /// emulator: gateway mode, the emulator's development certificate accepted, and the client limited to the
    /// endpoint it was given.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="provider">Configuration provider passed on to the Azure configuration loader; <c>null</c> uses its default provider.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddDockerAzure(this IServiceCollection services, IConfigProvider? provider)
        => services
            .AddAzure(provider)
            .ConfigureCosmosClientOptions(_ => CosmosEmulator.ClientOptions());
}
