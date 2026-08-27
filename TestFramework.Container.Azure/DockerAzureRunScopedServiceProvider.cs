using System;

namespace TestFramework.Container.Azure;

/// <summary>
/// Makes the environment itself resolvable for the length of one run.
/// </summary>
/// <remarks>
/// <para>
/// It used to do one more thing, and that thing was most of it: a caller resolving
/// <c>ConfigStore&lt;T&gt;</c> got one synthesised on the spot, seeded from whatever the definitions
/// named. That was the only way a container-only resource could reach anybody, back when reading
/// configuration meant reading a store.
/// </para>
/// <para>
/// A definition declares to the run now, so the synthesised store was a second answer to a question that
/// already had one - and the weaker of the two, since it held declarations and never saw an address any
/// container published. Two channels for one thing is the shape this whole migration has been removing,
/// and leaving the losing one in place is how it would have come back.
/// </para>
/// </remarks>
internal sealed class DockerAzureRunScopedServiceProvider(DockerAzureEnvironment environment, IServiceProvider baseServiceProvider) : IServiceProvider
{
    public object? GetService(Type serviceType)
        => serviceType == typeof(DockerAzureEnvironment) ? environment : baseServiceProvider.GetService(serviceType);
}
