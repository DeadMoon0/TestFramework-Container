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
| `ContainerSource` | declares where an application comes from: an image, a project, a directory, a type |
| `ContainerSourcePlan` | what will be done to get it there, stated before anything happens |
| `ContainerImageBuilder` | carries a plan out and produces an image or a directory |
| `ProjectQuery` | asks MSBuild what a project is, instead of inferring it from paths |
| `OfflineFeed` | hands a container the packages the host restored, so a build needs no credentials |
| `ContainerNetworkFactory` | creates the uniquely named network one environment's containers share |
| `ContainerEndpoints` | the two addresses every container has: host-mapped and network-alias |
| `ContainerReadiness` | waits until an HTTP endpoint or a SQL database actually answers |
| `ContainerOutputResolver` | locates an already-built output; the inferring road, kept for older definitions |
| `ContainerLogCapture` | writes a container's output into the run log before it is removed |
| `ContainerDockerCommands` | runs the Docker CLI and removes containers and networks reliably |
| `ContainerDockerHost` | points the client at the Docker Desktop named pipe a Windows machine uses |
| `ContainerRuntime` | does that once per process, and both network components call it first |
| `ContainerPortBinding` | publishes ports on `127.0.0.1` when the daemon is on this machine |
| `ContainerStartCoordinator` | starts one component's containers at once and cleans up a failed batch |
| `MsSqlContainerFactory` | builds a SQL Server container from shared settings |

## Two Things That Happen Without Being Asked

`ContainerRuntime.EnsureInitialized` runs as the first statement of the network component in both the
Azure and the Web environment, which is the root every other component depends on. On Windows it points
`DOCKER_HOST` at whichever Docker Desktop named pipe exists, because the client library does not probe
for it and the name differs between installations. It changes nothing when `DOCKER_HOST` is already set,
and it logs the pipe it chose.

`ContainerPortBinding` publishes every port on `127.0.0.1` rather than on all interfaces, so a SQL
Server or a storage emulator a test brought up is not reachable from the rest of the network for the
length of the run. It falls back to `0.0.0.0` when the daemon is remote or in Docker-in-Docker, where
the test process reaches the container over the network. Set `TESTFRAMEWORK_CONTAINER_HOST_IP` to decide
explicitly.

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

## Getting An Application Into A Container

An application's source is **declared**, not discovered:

```csharp
ContainerSource.Image("orders-api:ci-1234")                     // already an image
ContainerSource.Project("../Orders.Api/Orders.Api.csproj")      // the framework builds it
ContainerSource.Directory(@"C:\out\orders-api")                 // this exact folder
ContainerSource.EntryPoint<OrdersApiMarker>()                   // inferred from a loaded assembly
```

A relative project path resolves against **the source file that declares it**, captured at compile
time, so it reads the way it looks in the repository rather than depending on a working directory.

`ContainerSource.Project(...)` needs no reference from the test project to the application, which is
what keeps the application a black box. Everything else is read from the project by MSBuild — target
framework, assembly name, whether it uses the web SDK, its project references — so a custom output
path or the `artifacts/` layout costs nothing.

### Three ways to build a project

```csharp
ContainerSource.Project(path)                 // .BuiltAsImage() — the default
ContainerSource.Project(path).BuiltOnHost()
ContainerSource.Project(path).BuiltInContainer()
```

| Strategy | How | Needs | Host artifacts |
|---|---|---|---|
| `BuiltAsImage` | the SDK builds the image (`-t:PublishContainer`), no Dockerfile | SDK + a `docker` executable | `bin`/`obj` |
| `BuiltOnHost` | publishes to a temp directory, copied into a runtime image | SDK | `bin`/`obj` + temp, deleted after |
| `BuiltInContainer` | a generated Dockerfile builds it in Docker | SDK for the restore, a daemon | **none** |

**`BuiltInContainer` needs no feed credentials.** The host restores with the configuration that
already works; the packages that produced are copied into the build context as an *extracted cache*,
and the generated `NuGet.config` clears every source. The build cannot reach a feed and cannot
resolve anything the host did not.

It carries one constraint, checked while planning rather than left to fail inside the container: the
target framework generation must match the SDK on the machine, because which packages count as
framework-provided is SDK-version-dependent, and a mismatch asks for packages the handed-over cache
does not contain.

### The plan is stated before anything happens

```csharp
ContainerSourcePlan plan = await ContainerSourceResolver.PlanAsync(source, cancellationToken);
foreach (string line in plan.ToLogLines("orders"))
    logger.LogInformation(line);
```

```
'orders' source plan
  kind            Project (image built by the SDK)
  project         C:\src\Orders.Api\Orders.Api.csproj
  configuration   Release
  framework       net10.0
  runtime         mcr.microsoft.com/dotnet/aspnet:10.0
  derived         framework from the project's single target, 'net10.0'
  derived         runtime image from the project using the web SDK
```

Planning has no side effects — nothing is built, pulled or started — so "what would this run" is
answerable without waiting for Docker. Values that were worked out rather than declared say so.

A project that targets several frameworks is a **hard error**, not a silent pick: adding a framework
to a project must not quietly change what a test runs.

### The inferring road

`ContainerSource.EntryPoint<T>()` and `ContainerOutputResolver` remain for definitions written before
a source could be declared. They infer the project from a loaded assembly, which requires the test
project to reference the application and can be wrong in ways that only appear as a container that
fails to start. `ResolveProjectOutput` prefers the application's own `bin` over the test project's
copy of its assembly; every inference it makes is named in the plan.
