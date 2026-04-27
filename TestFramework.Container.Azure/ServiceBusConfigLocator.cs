namespace TestFramework.Container.Azure;

internal static class ServiceBusConfigLocator
{
    internal static string Resolve(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        foreach (string candidate in GetCandidates(configuredPath))
        {
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"Service Bus emulator topology file '{configuredPath}' was not found.", Path.Combine(AppContext.BaseDirectory, configuredPath));
    }

    private static IEnumerable<string> GetCandidates(string configuredPath)
    {
        yield return Path.Combine(AppContext.BaseDirectory, configuredPath);
        yield return Path.Combine(Environment.CurrentDirectory, configuredPath);

        string normalizedConfiguredPath = configuredPath.Replace('\\', '/');
        if (normalizedConfiguredPath.StartsWith("AzureDocker/", StringComparison.OrdinalIgnoreCase))
            yield return Path.Combine(AppContext.BaseDirectory, normalizedConfiguredPath["AzureDocker/".Length..].Replace('/', Path.DirectorySeparatorChar));

        for (DirectoryInfo? current = new(AppContext.BaseDirectory); current is not null; current = current.Parent)
            yield return Path.Combine(current.FullName, configuredPath);
    }
}