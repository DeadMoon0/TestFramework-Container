using System;
using System.Collections.Generic;
using System.IO;
namespace TestFramework.Container.Azure;

internal static class LogicAppPathLocator
{
    internal static string Resolve(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath) && Directory.Exists(configuredPath))
            return configuredPath;

        foreach (string candidate in GetCandidates(configuredPath))
        {
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new DirectoryNotFoundException($"Logic App path '{configuredPath}' was not found.");
    }

    private static IEnumerable<string> GetCandidates(string configuredPath)
    {
        yield return Path.Combine(AppContext.BaseDirectory, configuredPath);
        yield return Path.Combine(Environment.CurrentDirectory, configuredPath);

        for (DirectoryInfo? current = new(AppContext.BaseDirectory); current is not null; current = current.Parent)
            yield return Path.Combine(current.FullName, configuredPath);

        for (DirectoryInfo? current = new(Environment.CurrentDirectory); current is not null; current = current.Parent)
            yield return Path.Combine(current.FullName, configuredPath);
    }
}