namespace TestFramework.Container.Azure;

internal static class ServiceBusConfigLocator
{
    internal static string Resolve(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        string candidate = Path.Combine(AppContext.BaseDirectory, configuredPath);
        if (File.Exists(candidate))
            return candidate;

        throw new FileNotFoundException($"Service Bus emulator topology file '{configuredPath}' was not found.", candidate);
    }
}