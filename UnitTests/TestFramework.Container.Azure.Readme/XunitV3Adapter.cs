using System;
using System.Threading.Tasks;
using TestFramework.Container.Azure.Readme.Hosted;

namespace TestFramework.Container.Azure.Readme.XunitV3Adapter;

/// <summary>
/// The shape of xunit v3's <c>IAsyncLifetime</c>, which this v2 project cannot reference - enough to prove the
/// adapter below compiles against the fixture's real methods.
/// </summary>
public interface IAsyncLifetime : IAsyncDisposable
{
    ValueTask InitializeAsync();
}

#region snippet:xunit-v3-adapter
public sealed class DockerAzureHostedFixture : DockerAzureHostedCollectionFixture<DockerAzureHostedFixtureState>, IAsyncLifetime
{
    ValueTask IAsyncLifetime.InitializeAsync() => new(InitializeAsync());
    ValueTask IAsyncDisposable.DisposeAsync() => new(DisposeAsync());
}
#endregion
