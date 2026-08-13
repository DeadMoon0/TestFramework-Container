using System;
using System.Collections.Generic;
using System.Linq;
using DotNet.Testcontainers.Containers;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Web;

/// <summary>
/// One running application container.
/// </summary>
/// <param name="Identifier">The API identifier it serves.</param>
/// <param name="Container">The running container.</param>
/// <param name="BaseUrl">The address the test process reaches it at.</param>
/// <param name="ShippedDirectory">The build output that was copied into it.</param>
/// <param name="ShippedBuiltAtUtc">When the shipped assembly was last written.</param>
/// <param name="SettingsFileName">The name of the generated settings file.</param>
/// <param name="SettingsJson">The exact configuration the application was given.</param>
/// <remarks>
/// The shipped directory, its age and the settings content are kept so a test can state what
/// actually ran, rather than having to infer it from a container that may already be gone. Nothing
/// in this package builds the application, so the timestamp is the only evidence that what ran was
/// not a stale build.
/// </remarks>
public sealed record RunningApi(
    string Identifier,
    IContainer Container,
    Uri BaseUrl,
    string ShippedDirectory,
    DateTimeOffset? ShippedBuiltAtUtc,
    string SettingsFileName,
    string SettingsJson);

/// <summary>
/// The application containers a run started.
/// </summary>
public sealed class ApiComponentState
{
    internal ApiComponentState(IReadOnlyList<RunningApi> apis)
    {
        Apis = apis;
    }

    /// <summary>
    /// The running applications, in the order they were started.
    /// </summary>
    public IReadOnlyList<RunningApi> Apis { get; }

    /// <summary>
    /// Returns a running application by identifier.
    /// </summary>
    /// <param name="identifier">The API identifier.</param>
    /// <exception cref="FrameworkStateException">No application was started for the identifier.</exception>
    public RunningApi GetRequiredApi(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);

        RunningApi? api = Apis.FirstOrDefault(candidate => string.Equals(candidate.Identifier, identifier, StringComparison.Ordinal));
        if (api is not null)
            return api;

        throw new FrameworkStateException($"No application was started for the API identifier '{identifier}'. Started: {(Apis.Count == 0 ? "none" : string.Join(", ", Apis.Select(candidate => candidate.Identifier)))}.");
    }
}
