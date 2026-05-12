using System;
using System.Collections.Generic;
using System.Reflection;
using TestFramework.Azure.Configuration;
using TestFramework.Azure.LogicApp;

namespace TestFramework.Container.Azure;

internal sealed class DockerAzureRunScopedServiceProvider(DockerAzureEnvironment environment, IServiceProvider baseServiceProvider, IServiceProvider? fallbackConfigServiceProvider = null) : IServiceProvider
{
    private static readonly MethodInfo GetOrCreateConfigStoreMethod = typeof(DockerAzureEnvironment)
        .GetMethod(nameof(DockerAzureEnvironment.GetOrCreateConfigStore), BindingFlags.Instance | BindingFlags.NonPublic)!;

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(DockerAzureEnvironment)
            || serviceType == typeof(ILogicAppWorkflowMetadataProvider))
        {
            return environment;
        }

        object? service = baseServiceProvider.GetService(serviceType);
        if (service is not null)
            return service;

        if (!serviceType.IsGenericType || serviceType.GetGenericTypeDefinition() != typeof(ConfigStore<>))
            return null;

        Type configType = serviceType.GetGenericArguments()[0];
        IReadOnlyCollection<string> identifiers = environment.GetUsedIdentifiersFor(configType);
        if (identifiers.Count == 0)
            return null;

        IServiceProvider storeProvider = fallbackConfigServiceProvider is null
            ? baseServiceProvider
            : new ConfigStoreFallbackServiceProvider(baseServiceProvider, fallbackConfigServiceProvider);

        return GetOrCreateConfigStoreMethod
            .MakeGenericMethod(configType)
            .Invoke(environment, [storeProvider, identifiers, "Timeline run service resolution"]);
    }

    private sealed class ConfigStoreFallbackServiceProvider(IServiceProvider primary, IServiceProvider fallback) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            return primary.GetService(serviceType) ?? fallback.GetService(serviceType);
        }
    }
}
