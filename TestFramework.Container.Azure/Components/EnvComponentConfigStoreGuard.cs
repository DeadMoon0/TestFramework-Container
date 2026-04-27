using TestFramework.Azure.Configuration;

namespace TestFramework.Container.Azure.Components;

internal static class EnvComponentConfigStoreGuard
{
    public static ConfigStore<TConfig>? GetRequiredStore<TConfig>(IServiceProvider serviceProvider, IReadOnlyCollection<string> identifiers, string componentName)
    {
        if (identifiers.Count == 0)
            return null;

        return serviceProvider.GetService(typeof(ConfigStore<TConfig>)) as ConfigStore<TConfig>
            ?? throw new InvalidOperationException($"{componentName} requires ConfigStore<{typeof(TConfig).Name}> when identifiers are in use: {string.Join(", ", identifiers.OrderBy(x => x, StringComparer.Ordinal))}.");
    }
}