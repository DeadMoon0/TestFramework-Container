using System;
using System.Collections.Generic;
using System.Linq;
using TestFramework.Core.Environment.Graph;
using TestFramework.Core.Exceptions;
using TestFramework.Core.Steps;

namespace TestFramework.Container.Azure.Components;

/// <summary>
/// Refuses to start a container for a resource this run cannot answer for.
/// </summary>
/// <remarks>
/// <para>
/// The check is worth keeping and the thing it used to check is not. It asked whether a configuration
/// store held an entry, and told a caller to register one - which stopped being true when a run started
/// answering for its own configuration, so the advice named a channel that reaches nothing. Following it
/// exactly would have left the caller where they started, which is the worst kind of error message.
/// </para>
/// <para>
/// It asks the run instead. Two things can supply a resource now - a configuration entry, or a definition
/// that names it - and the message says both, because from here they are the same answer.
/// </para>
/// <para>
/// Early on purpose: this runs before a container is started, so a resource nobody described costs a
/// sentence rather than an emulator, a readiness wait, and a step timing out against an address that was
/// never going to exist.
/// </para>
/// </remarks>
internal static class EnvComponentResourceGuard
{
    /// <summary>
    /// Checks that the run supplies every identifier a component is about to serve.
    /// </summary>
    /// <param name="context">The run.</param>
    /// <param name="kind">Which kind of resource.</param>
    /// <param name="identifiers">The identifiers this run decided it needs.</param>
    /// <param name="componentName">The component, for the message.</param>
    /// <exception cref="FrameworkConfigurationException">The run supplies nothing for one of them.</exception>
    public static void EnsureSupplied(RunContext context, ResourceKind kind, IReadOnlyCollection<string> identifiers, string componentName)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(identifiers);

        foreach (string identifier in identifiers.OrderBy(identifier => identifier, StringComparer.Ordinal))
        {
            if (context.Values.ValuesFor(kind, identifier, ResourceVantage.Host).Count > 0)
            {
                continue;
            }

            throw new FrameworkConfigurationException(
                $"{componentName} needs {kind}/'{identifier}', and nothing in this run supplies it.",
                [
                    $"Add the configuration entry for '{identifier}', or give the definition that provisions it the names it should use.",
                ],
                [.. context.Values.IdentifiersOf(kind).Select(known => $"{kind}/'{known}'")]);
        }
    }
}
