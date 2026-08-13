![Icon](https://raw.githubusercontent.com/DeadMoon0/TestFramework-Common/96ef4240c1e55ba95a20b99285219a61407c6355/Assets/Icon.svg)

# TestFramework.Container

Shared Docker building blocks for TestFramework container environments.

This package holds what every container-backed environment needs and nothing specific to any one of
them. `TestFramework.Container.Azure` and the web container environment are both built on it.

You normally consume one of those packages rather than this one directly. Reach for it when writing
an environment component of your own.

## Install

```bash
dotnet add package TestFramework.Container
```

Targets `net8.0` and `net10.0`.

## What It Provides

| Type | Purpose |
|---|---|
| `ContainerNetworkFactory` | creates the uniquely named network one environment's containers share |
| `ContainerEndpoints` | the two addresses every container has: host-mapped and network-alias |
| `ContainerReadiness` | waits until an HTTP endpoint or a SQL database actually answers |
| `ContainerOutputResolver` | locates a project's build output and its target framework, for shipping into a container |
| `ContainerLogCapture` | writes a container's output into the run log before it is removed |
| `ContainerDockerCommands` | removes containers and networks reliably, falling back to disposal |
| `ContainerDockerHost` | points the client at the Docker Desktop named pipe a Windows machine uses |
| `MsSqlContainerFactory` | builds a SQL Server container from shared settings |

## The One Rule Worth Knowing

A container has **two** addresses, and using the wrong one is the most common defect in a
container-backed test setup:

```csharp
// The test process reaches the container through the mapped port on the Docker host.
Uri fromTest = ContainerEndpoints.HostEndpoint(container, 8080);

// Another container on the same network reaches it by alias and internal port.
Uri fromContainer = ContainerEndpoints.NetworkEndpoint("orders-api", 8080);
```

The same applies to connection strings: publish `HostSqlConnectionString(...)` to the test process,
and inject `NetworkSqlConnectionString(...)` into the application container's settings. Both describe
the same database.

## Readiness Is Not Startup

A started container is not a usable one. Components wait before publishing an endpoint, so a startup
race surfaces as a clear timeout rather than as a confusing failure in the first step that uses it:

```csharp
await ContainerReadiness.WaitForHttpAsync(baseAddress, "/health", TimeSpan.FromMinutes(2), "orders", logger, cancellationToken);
await ContainerReadiness.WaitForSqlStatementAsync(connectionString, "SELECT DB_NAME();", TimeSpan.FromMinutes(2), "main", cancellationToken);
```

## Shipping Build Output

`ContainerOutputResolver` finds the assemblies to ship and the framework they were built for, so the
runtime image can be chosen to match instead of being hardcoded:

```csharp
// Prefers the location the assembly was loaded from.
ContainerOutput loaded = ContainerOutputResolver.Resolve(typeof(Program), "host.json");

// Prefers the owning project's own bin. Use this for an application under test.
ContainerOutput owned = ContainerOutputResolver.ResolveProjectOutput(typeof(ApiMarker));

// output.OutputDirectory, output.TargetFramework ("net10.0"), output.AssemblyFileName
```

The distinction matters: a project-referenced assembly is copied into the referencing project's
output as well, so resolving from the loaded location lands in a *test project's* bin — complete
enough to start, full of assemblies the application does not need, and misleading about what was
shipped. `ResolveProjectOutput` prefers the application's own output and falls back to the loaded
location only when that is incomplete.

When the required files are in neither place, resolution fails with every path it examined rather
than starting a container that would fail obscurely. Prefer stating the directory explicitly where
the consuming API allows it — resolution is a convenience, not a contract.
