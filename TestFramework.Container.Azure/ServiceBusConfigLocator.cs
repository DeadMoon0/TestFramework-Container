using System;
using System.Collections.Generic;
using System.IO;
using TestFramework.Core.Exceptions;
using TestFramework.Core.Logging;

namespace TestFramework.Container.Azure;

/// <summary>
/// Finds the Service Bus emulator topology file a relative path refers to.
/// </summary>
/// <remarks>
/// The search used to climb every ancestor to the drive root, so a directory two levels above the
/// repository that happened to contain <c>Configurations/ServiceBus/config.json</c> would win, silently,
/// and the emulator would come up with somebody else's entities. The climb now stops at the repository
/// boundary, every candidate is logged, and the winner is named.
/// </remarks>
internal static class ServiceBusConfigLocator
{
    internal static string Resolve(string configuredPath, ScopedLogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);

        if (Path.IsPathRooted(configuredPath))
        {
            if (File.Exists(configuredPath))
            {
                logger?.LogInformation($"The Service Bus emulator topology is the absolute path '{configuredPath}'.");
                return configuredPath;
            }

            throw new FrameworkConfigurationException(
                $"The Service Bus emulator topology file '{configuredPath}' does not exist.",
                ["An absolute path is used as given; nothing is searched for it."]);
        }

        List<string> probed = [];
        foreach (string candidate in GetCandidates(configuredPath))
        {
            probed.Add(candidate);
            if (!File.Exists(candidate))
                continue;

            // Naming the winner is the point: the file that gets picked decides which entities the
            // emulator comes up with, and picking the wrong one used to be invisible.
            logger?.LogInformation($"The Service Bus emulator topology '{configuredPath}' resolved to '{candidate}', the first of {probed.Count} candidate(s) that exists.");
            return candidate;
        }

        throw new FrameworkConfigurationException(
            $"The Service Bus emulator topology file '{configuredPath}' was not found.",
            [
                "The search starts at the build output and climbs to the repository boundary; it does not go above it.",
                "Copy the file to the output directory, or point at it with an absolute path.",
            ],
            [$"Checked: {string.Join(", ", probed)}"]);
    }

    private static IEnumerable<string> GetCandidates(string configuredPath)
    {
        yield return Path.Combine(AppContext.BaseDirectory, configuredPath);
        yield return Path.Combine(Environment.CurrentDirectory, configuredPath);

        // Climbing stops at the repository, so a coincidentally named file above it cannot win.
        for (DirectoryInfo? current = new(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            yield return Path.Combine(current.FullName, configuredPath);

            if (ContainerRepositoryBoundary.IsBoundary(current))
                yield break;
        }
    }
}
