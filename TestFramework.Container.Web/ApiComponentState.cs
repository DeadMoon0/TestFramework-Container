using System;
using System.Collections.Generic;
using System.Linq;
using DotNet.Testcontainers.Containers;
using TestFramework.Container.Sources;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Web;

/// <summary>
/// One running application container.
/// </summary>
/// <remarks>
/// The plan and the settings content are kept so a test can state what actually ran, rather than
/// having to infer it from a container that may already be gone.
/// </remarks>
public sealed record RunningApi
{
    internal RunningApi(string identifier, IContainer container, Uri baseUrl, Uri networkBaseUrl, ContainerSourcePlan plan, string settingsFileName, string settingsJson)
    {
        this.Identifier = identifier;
        this.Container = container;
        this.BaseUrl = baseUrl;
        this.NetworkBaseUrl = networkBaseUrl;
        this.Plan = plan;
        this.SettingsFileName = settingsFileName;
        this.SettingsJson = settingsJson;
    }

    /// <summary>
    /// The API identifier it serves.
    /// </summary>
    public string Identifier { get; }

    /// <summary>
    /// The running container.
    /// </summary>
    public IContainer Container { get; }

    /// <summary>
    /// The address the test process reaches it at.
    /// </summary>
    public Uri BaseUrl { get; }

    /// <summary>
    /// The address another container on the environment's network reaches it at.
    /// </summary>
    public Uri NetworkBaseUrl { get; }

    /// <summary>
    /// What was done to get the application into the container.
    /// </summary>
    public ContainerSourcePlan Plan { get; }

    /// <summary>
    /// The name of the generated settings file.
    /// </summary>
    public string SettingsFileName { get; }

    /// <summary>
    /// The exact configuration the application was given.
    /// </summary>
    public string SettingsJson { get; }
}

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
