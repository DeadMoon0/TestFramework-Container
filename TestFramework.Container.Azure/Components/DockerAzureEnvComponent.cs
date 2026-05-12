using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TestFramework.Core.Environment;

namespace TestFramework.Container.Azure.Components;

internal abstract class DockerAzureEnvComponent : EnvComponent
{
    protected DockerAzureEnvironment GetDockerEnvironment(IEnvironmentProvider environment)
    {
        if (environment is DockerAzureEnvironment dockerEnvironment)
            return dockerEnvironment;

        if (environment is IEnvironmentProviderProxy proxy)
            return GetDockerEnvironment(proxy.InnerEnvironment);

        throw new InvalidOperationException($"Environment component '{Id}' requires {nameof(DockerAzureEnvironment)}.");
    }

    protected static async Task ForceRemoveContainerAsync(IContainer container, CancellationToken cancellationToken)
    {
        try
        {
            CommandResult result = await RunDockerCommandAsync($"rm -f {container.Id}", cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 0)
                return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }

        await container.DisposeAsync().ConfigureAwait(false);
    }

    protected static async Task ForceRemoveNetworkAsync(INetwork network, CancellationToken cancellationToken)
    {
        try
        {
            CommandResult result = await RunDockerCommandAsync($"network rm {network.Name}", cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 0)
                return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }

        await network.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task<CommandResult> RunDockerCommandAsync(string arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ProcessStartInfo startInfo = new()
        {
            FileName = "docker",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        NormalizeDockerHost(startInfo.Environment);

        using Process process = new()
        {
            StartInfo = startInfo,
        };

        process.Start();
        string standardOutput = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        string standardError = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new CommandResult(process.ExitCode, standardOutput, standardError);
    }

    private static void NormalizeDockerHost(IDictionary<string, string?> environment)
    {
        const string dockerHost = "DOCKER_HOST";

        if (!environment.TryGetValue(dockerHost, out string? value) || string.IsNullOrWhiteSpace(value))
            return;

        if (value.StartsWith("npipe://./pipe/", StringComparison.OrdinalIgnoreCase))
            environment[dockerHost] = $"npipe:////./pipe/{value[15..]}";
    }

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);
}