using System;
using System.Collections.Generic;
using System.Linq;
using DotNet.Testcontainers.Containers;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Web;

/// <summary>
/// One running stub server.
/// </summary>
/// <param name="Identifier">The stub identifier it serves.</param>
/// <param name="Container">The running container.</param>
/// <param name="HostBaseUrl">The address the test process reaches it at.</param>
/// <param name="NetworkBaseUrl">The address another container reaches it at.</param>
/// <param name="MappingCount">How many mappings the server actually loaded.</param>
public sealed record RunningStub(
    string Identifier,
    IContainer Container,
    Uri HostBaseUrl,
    Uri NetworkBaseUrl,
    int MappingCount);

/// <summary>
/// The stub servers a run started.
/// </summary>
public sealed class StubComponentState
{
    internal StubComponentState(IReadOnlyList<RunningStub> stubs)
    {
        Stubs = stubs;
    }

    /// <summary>
    /// The running stubs, in the order they were started.
    /// </summary>
    public IReadOnlyList<RunningStub> Stubs { get; }

    /// <summary>
    /// Returns a running stub by identifier.
    /// </summary>
    /// <param name="identifier">The stub identifier.</param>
    /// <exception cref="FrameworkStateException">No stub was started for the identifier.</exception>
    public RunningStub GetRequiredStub(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);

        RunningStub? stub = Stubs.FirstOrDefault(candidate => string.Equals(candidate.Identifier, identifier, StringComparison.Ordinal));
        if (stub is not null)
            return stub;

        throw new FrameworkStateException($"No stub was started for the identifier '{identifier}'. Started: {(Stubs.Count == 0 ? "none" : string.Join(", ", Stubs.Select(candidate => candidate.Identifier)))}.");
    }
}
