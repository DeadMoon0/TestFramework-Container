using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container;

/// <summary>
/// Answers what a built assembly was built for.
/// </summary>
/// <remarks>
/// This is all that is left of the output-inference machinery: everything a container ships is
/// declared through a <c>ContainerSource</c> now, so nothing discovers build output from where an
/// assembly happened to be loaded any more. Reading the target framework off an assembly stays,
/// because a test that builds a project for "the framework this test host runs" states exactly that.
/// </remarks>
public static class ContainerOutputResolver
{
    /// <summary>
    /// Returns the target framework moniker an assembly was built for.
    /// </summary>
    /// <param name="assembly">The assembly to inspect.</param>
    public static string ResolveTargetFramework(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        string? frameworkName = assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
        if (string.IsNullOrWhiteSpace(frameworkName))
            throw new FrameworkConfigurationException($"Assembly '{assembly.GetName().Name}' does not declare a target framework, so no matching runtime image can be chosen.");

        // ".NETCoreApp,Version=v10.0" -> "net10.0"
        string[] parts = frameworkName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string version = parts.FirstOrDefault(part => part.StartsWith("Version=v", StringComparison.OrdinalIgnoreCase))?["Version=v".Length..]
            ?? throw new FrameworkConfigurationException($"The target framework '{frameworkName}' could not be parsed into a moniker.");

        return $"net{version}";
    }
}
