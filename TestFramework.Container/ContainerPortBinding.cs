using System;
using System.Collections.Generic;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Configurations;

namespace TestFramework.Container;

/// <summary>
/// Publishes container ports on the loopback address instead of every interface.
/// </summary>
/// <remarks>
/// <para>
/// A published port with no host address is published on <c>0.0.0.0</c>, so a SQL Server, a storage
/// emulator or an application a test brought up is reachable from the whole network for as long as the
/// run lasts. The credentials those containers use are the same on every machine that runs the suite.
/// </para>
/// <para>
/// Testcontainers 4.11 has no host-address overload on <c>WithPortBinding</c>, so the address is written
/// onto the create parameters instead. The modifier runs after the builder has configured the request,
/// which is the only point at which every binding the builder added is visible.
/// </para>
/// <para>
/// Loopback is only correct when the daemon runs on the machine the test process runs on. Against a
/// remote daemon or Docker-in-Docker the test process reaches the container over the network, so the
/// binding stays on every interface. Set <c>TESTFRAMEWORK_CONTAINER_HOST_IP</c> to decide explicitly.
/// </para>
/// </remarks>
public static class ContainerPortBinding
{
    /// <summary>
    /// The environment variable that overrides the address ports are published on.
    /// </summary>
    public const string HostIpVariable = "TESTFRAMEWORK_CONTAINER_HOST_IP";

    private const string Loopback = "127.0.0.1";
    private const string AllInterfaces = "0.0.0.0";

    private static readonly Lazy<string> ResolvedHostIp = new(ResolveHostIp, isThreadSafe: true);

    /// <summary>
    /// The address published ports are bound to.
    /// </summary>
    public static string HostIp => ResolvedHostIp.Value;

    /// <summary>
    /// Rewrites every port binding that has no host address of its own.
    /// </summary>
    /// <param name="parameters">The container creation request the builder assembled.</param>
    /// <remarks>
    /// Pass this to <c>WithCreateParameterModifier</c> on any builder that publishes a port.
    /// </remarks>
    public static void Apply(CreateContainerParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        string hostIp = HostIp;
        IDictionary<string, IList<PortBinding>>? portBindings = parameters.HostConfig?.PortBindings;
        if (portBindings is null)
            return;

        foreach (IList<PortBinding> bindings in portBindings.Values)
        {
            if (bindings is null)
                continue;

            foreach (PortBinding binding in bindings)
            {
                // An address the caller set deliberately outranks the default.
                if (string.IsNullOrEmpty(binding.HostIP))
                    binding.HostIP = hostIp;
            }
        }
    }

    private static string ResolveHostIp()
    {
        string? configured = Environment.GetEnvironmentVariable(HostIpVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        try
        {
            // A host override exists precisely because the daemon is not where the test process is.
            if (!string.IsNullOrWhiteSpace(TestcontainersSettings.DockerHostOverride))
                return AllInterfaces;

            // The resolved endpoint accounts for the Docker context as well as the environment, but it
            // is only available once an auth provider has answered. DOCKER_HOST is the fallback, and
            // its absence means the machine's own default socket or pipe.
            Uri? endpoint = TestcontainersSettings.OS?.DockerEndpointAuthConfig?.Endpoint ?? ReadDockerHostVariable();
            if (endpoint is null)
                return Loopback;

            // A pipe or a socket cannot be anywhere but this machine.
            if (string.Equals(endpoint.Scheme, "npipe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(endpoint.Scheme, "unix", StringComparison.OrdinalIgnoreCase))
                return Loopback;

            return endpoint.IsLoopback ? Loopback : AllInterfaces;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Not knowing where the daemon is must never stop a run, and publishing on every interface
            // is the behaviour that always worked.
            return AllInterfaces;
        }
    }

    private static Uri? ReadDockerHostVariable()
    {
        string? dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
        if (string.IsNullOrWhiteSpace(dockerHost))
            return null;

        return Uri.TryCreate(dockerHost, UriKind.Absolute, out Uri? parsed) ? parsed : null;
    }
}
