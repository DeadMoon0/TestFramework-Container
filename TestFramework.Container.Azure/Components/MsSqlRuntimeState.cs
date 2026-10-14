using Testcontainers.MsSql;

namespace TestFramework.Container.Azure.Components;

/// <summary>
/// A running SQL Server container and the <c>sa</c> password it was started with.
/// </summary>
/// <remarks>
/// The password travels with the container because the Service Bus emulator logs into this server too. It used to
/// live on the environment instance, which two runs sharing it would both claim, and which the hosted road had to
/// copy into every clone so a run could still authenticate against the server the bootstrap had started.
/// </remarks>
/// <param name="Container">The container.</param>
/// <param name="Password">The <c>sa</c> password.</param>
internal sealed record MsSqlRuntimeState(MsSqlContainer Container, string Password);
