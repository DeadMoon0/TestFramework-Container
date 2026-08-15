using Microsoft.Extensions.DependencyInjection;
using TestFramework.Container.Azure;
using TestFramework.Container.Azure.Components;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;
using TestFramework.Core.Exceptions;
using TestFramework.Core.Logging;
using TestFramework.Core.Variables;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace TestFramework.Container.Azure.Tests;

public class ContainerGuardTests
{
    [Fact]
    public void ConnectionStringGuards_RejectServiceBusConnectionWithoutEmulatorFlag()
    {
        FrameworkConfigurationException exception = Assert.Throws<FrameworkConfigurationException>(() =>
            ConnectionStringGuards.EnsureServiceBus("Endpoint=sb://127.0.0.1/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=key=;"));

        Assert.Contains("UseDevelopmentEmulator=true", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionStringGuards_RejectServiceBusConnectionToRemoteHost()
    {
        FrameworkConfigurationException exception = Assert.Throws<FrameworkConfigurationException>(() =>
            ConnectionStringGuards.EnsureServiceBus("Endpoint=sb://contoso.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=key=;UseDevelopmentEmulator=true;"));

        Assert.Contains("local Docker emulator endpoint", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DockerAzureEnvironment_GetRequiredRuntimeState_ThrowsHelpfulMessageWhenMissing()
    {
        DockerAzureEnvironment environment = new();

        FrameworkStateException exception = Assert.Throws<FrameworkStateException>(() =>
            environment.GetRequiredRuntimeState<object>(DockerAzureEnvironment.AzuriteComponentId));

        Assert.Contains(DockerAzureEnvironment.AzuriteComponentId.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AzuriteEnvComponent_CreateAsync_ThrowsHelpfulMessageForWrongEnvironmentType()
    {
        AzuriteEnvComponent component = new();

        FrameworkStateException exception = await Assert.ThrowsAsync<FrameworkStateException>(() =>
            component.CreateAsync(
                new FakeEnvironmentProvider(),
                new ServiceCollection().BuildServiceProvider(),
                null!,
                null!,
                null!,
                CancellationToken.None));

        Assert.Contains(nameof(DockerAzureEnvironment), exception.Message, StringComparison.Ordinal);
        Assert.Contains(component.Id.ToString(), exception.Message, StringComparison.Ordinal);
    }

    private sealed class FakeEnvironmentProvider : IEnvironmentProvider
    {
        public IReadOnlyCollection<EnvComponentIdentifier> ResolveComponents(IEnumerable<ArtifactInstanceGeneric> artifacts, IEnumerable<EnvironmentRequirement> requirements)
            => [];

        public EnvComponent GetComponent(EnvComponentIdentifier identifier)
            => throw new NotSupportedException();
    }
}