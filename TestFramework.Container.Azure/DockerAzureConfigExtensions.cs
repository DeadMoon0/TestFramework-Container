using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using TestFramework.Azure.Configuration;
using TestFramework.Azure.Extensions;
using TestFramework.Config.Builder.InstanceBuilder;

namespace TestFramework.Container.Azure;

public static class DockerAzureConfigExtensions
{
    public static IConfigInstanceBuilder LoadDockerAzureConfig(this IConfigInstanceBuilder builder, IConfigProvider? provider = null)
    {
        return builder
            .LoadAzureConfig(provider)
            .AddService(static services => services.ConfigureDockerAzureCosmosEmulator());
    }

    public static IServiceCollection ConfigureDockerAzureCosmosEmulator(this IServiceCollection services)
    {
        services.ConfigureCosmosClientOptions(_ => new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            LimitToEndpoint = true,
            HttpClientFactory = static () => new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            }),
        });

        return services;
    }
}