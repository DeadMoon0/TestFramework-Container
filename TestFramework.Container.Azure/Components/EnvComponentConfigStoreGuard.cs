using System;
using System.Collections.Generic;
using TestFramework.Azure.Configuration;

namespace TestFramework.Container.Azure.Components;

internal static class EnvComponentConfigStoreGuard
{
    public static ConfigStore<TConfig>? GetRequiredStore<TConfig>(DockerAzureEnvironment dockerEnvironment, IServiceProvider serviceProvider, IReadOnlyCollection<string> identifiers, string componentName)
    {
        return dockerEnvironment.GetOrCreateConfigStore<TConfig>(serviceProvider, identifiers, componentName);
    }
}