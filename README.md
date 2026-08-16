![Icon](https://raw.githubusercontent.com/DeadMoon0/TestFramework-Common/96ef4240c1e55ba95a20b99285219a61407c6355/Assets/Icon.svg)
[![NuGet Version](https://img.shields.io/nuget/v/TestFramework.Container.Azure?label=nuget%20TestFramework.Container.Azure)](https://www.nuget.org/packages/TestFramework.Container.Azure)

# TestFramework-Container

`TestFramework.Container.Azure` lets a normal TestFramework timeline run against Docker-backed Azure emulators.

Use it when you want the Azure timeline shape from `TestFramework.Azure`, but you want the backing services to come from local containers instead of a live cloud environment.

That includes the emulator-backed storage, messaging, database, and function-host surfaces.

## Choose Your Path

Use the container package when the timeline should stay Azure-shaped, but the backing resources should be local.

- Already have a normal Azure timeline and want the smallest switch: keep the timeline, keep the identifiers, and add `SetEnv(DockerAzureEnvironment.For<TRootDefinition>())`.
- Starting with a Function App and common local dependencies: prefer the additive helpers such as `DockerAzureEnvironment.ForFunctionAppWithStorage<...>(...)`, `ForFunctionAppWithStorageAndServiceBus<...>(...)`, or `ForFunctionAppWithCommonBindings<...>(...)` before building a larger definition graph.
- Need real Azure resources or Logic Apps: stay on `TestFramework.Azure`; the container package does not own live Azure bootstrap.

## Packages In This Repository

| Package | Purpose |
|---|---|
| `TestFramework.Container` | shared Docker building blocks: network, host and network endpoint addressing, readiness waits, build-output resolution, log capture |
| `TestFramework.Container.Azure` | Docker-backed Azure emulators for TestFramework timelines, built on the above |
| `TestFramework.Container.Web` | Docker-backed ASP.NET applications and SQL Server for `TestFramework.Web` timelines, built on the same blocks — see [its README](./TestFramework.Container.Web/README.md) |

## Install

```bash
dotnet add package TestFramework.Container.Azure
```

## Source Of Truth

This repository-level README is the landing page.
The maintained onboarding guide, migration flow, golden sample, and composition guidance now live in [TestFramework.Container.Azure/README.md](./TestFramework.Container.Azure/README.md).

Use this file for:
- package identity at a glance
- the shortest possible first-use path
- links into the maintained package docs

## What It Does

The package plugs into the run builder through `SetEnv(...)` with a `DockerAzureEnvironment`.

That environment:
- starts only the emulator components the timeline actually needs
- rewrites the configured Azure connection settings to the mapped local Docker endpoints
- validates the resolved component graph and binds compatible contracts before startup
- keeps the normal identifier-driven Azure config contract intact

Those placeholder config entries can be registered directly by the test project, or owned by shared component classes when a reusable test stack wants each component to describe itself completely. The environment treats those configs as identifier registrations plus logical placeholders, then rewrites the runtime endpoints from the activated component graph.

The timeline still reads like a normal TestFramework timeline. The environment is the switch that makes the run container-backed.

## Limitations

**Logic Apps are not supported in the Docker container package.**

If your timeline includes Logic App triggers or events, keep those tests on the live `TestFramework.Azure` path with a real Azure-hosted Logic App. The container environment covers: Function Apps, Service Bus, Blob Storage, Table Storage, Cosmos DB, and SQL.

## Prerequisites

- Docker Desktop or another compatible Docker engine must be running
- the test project must already reference the normal Azure config identifiers it uses in the timeline
- Service Bus scenarios need a valid topology, preferably through `ConfigureServiceBusTopology(...)`
- Cosmos scenarios often need emulator-specific client options such as certificate bypass

## Start Here

If you are approaching the container package for the first time, keep the first pass deliberately small:

1. Keep the timeline unchanged from the normal Azure version.
2. Register the same named Azure config identifiers you would use against real Azure.
3. Add one root definition class for the first emulator-backed resource you need.
4. Switch the run to `SetEnv(DockerAzureEnvironment.For<TRootDefinition>())`.

That gives you the beginner path: same timeline, same identifiers, one explicit environment switch.
Only move into `Include<TDefinition>()`, contracts, and shared definition graphs once the first root-driven scenario is working.

If the first scenario is a local Function App, the next-smallest path is one of the built-in common-stack helpers instead of a custom root definition. Start there when you only need one Function App plus its usual bindings.

## Read Next

- Maintained onboarding and migration guide: [TestFramework.Container.Azure/README.md](./TestFramework.Container.Azure/README.md)
- Architecture and activation model: [TestFramework.Container.Azure/Documentation/Architecture.md](./TestFramework.Container.Azure/Documentation/Architecture.md)

## Smoke Tests

The normal Container.Azure test project includes the end-to-end smoke path as part of the full suite:

```powershell
dotnet test .\UnitTests\TestFramework.Container.Azure.Tests\TestFramework.Container.Azure.Tests.csproj -c Release
```

## Troubleshooting

- If the environment does not start at all, verify Docker availability before inspecting timeline code.
- If a resource is not created, check whether the timeline or environment requirements actually activate the owning definition.
- If a definition is included but never used, treat that as "available" rather than "activated".
- If Service Bus startup fails, inspect topology ownership and SQL dependency first.
- If Cosmos tests fail after the emulator starts, inspect client options and confirm the test process is using the rewritten mapped endpoint.

### "Failed to download" / CONTAINER1006 when building an image from a project

Building an image from `ContainerSource.Project(...)` makes the .NET SDK fetch the base image's
manifest from a registry over TLS, and it does so on every build even when that exact image is
already in the local Docker daemon. On some networks that one request fails while everything else
works, and the SDK spends about two and a half minutes retrying before reporting `CONTAINER1006`,
which says nothing about the cause.

The usual cause is a broken IPv6 path. The registry hostname resolves to both address families, the
connection is made over IPv6, TCP succeeds because the first packets are small, and the TLS
handshake then dies on the certificate chain because those packets exceed the real path MTU.
Routers cannot fragment IPv6, and the ICMPv6 message that would report it is often filtered, so the
packets simply vanish. A PPPoE line -- the normal shape of consumer DSL, with an MTU of 1492 rather
than 1500 -- behind a VPN is the classic setting. It is intermittent, because the client races both
families and IPv4 sometimes wins, which is why the occasional retry succeeds and hides the cause.

**You do not have to do anything about it.** This is detected and worked around:

- Before the SDK is started, the daemon is asked whether the base image is already here, and the
  registry is asked whether it answers. Both questions cost milliseconds.
- If the image is here and the registry is not answering, the application is published to a
  directory and the image is built from the local copy with `docker build`, which pulls only what is
  missing and therefore needs no registry at all.
- If the probe was wrong -- it is a hint, not a verdict -- the SDK is tried anyway and the same
  recovery runs after it fails, or after it stops making progress: the publish is bounded by a
  timeout and killed with its process tree. A registry that neither answers nor refuses does not
  make the SDK fail, it makes it wait, and an unbounded wait means nothing downstream ever gets to
  recover. Cancelling a run kills the publish too, rather than leaving it to idle for hours.
- Either way the run log says what was detected and what was done instead, and the resulting plan
  carries a fallback reason. Nothing about this is silent.

The workaround needs the base image to be in the local daemon. If it is not, fetch it once from a
network that works:

```bash
docker pull mcr.microsoft.com/dotnet/aspnet:8.0
```

From then on that machine builds without a registry. To fix the network itself, lower the MTU or
prefer IPv4 -- both need administrator rights. Where the machine cannot be changed at all, hand the
source a prebuilt image instead of a project with `ContainerSource.Image("my-api:local")`, which
fetches nothing at run time.
