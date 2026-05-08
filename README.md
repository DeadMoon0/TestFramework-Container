![Icon](https://raw.githubusercontent.com/DeadMoon0/TestFramework-Common/96ef4240c1e55ba95a20b99285219a61407c6355/Assets/Icon.svg)
[![NuGet Version](https://img.shields.io/nuget/v/TestFramework.Container.Azure?label=nuget%20TestFramework.Container.Azure)](https://www.nuget.org/packages/TestFramework.Container.Azure)

# TestFramework-Container

`TestFramework.Container.Azure` lets a normal TestFramework timeline run against Docker-backed Azure emulators.

Use it when you want the Azure timeline shape from `TestFramework.Azure`, but you want the backing services to come from local containers instead of a live cloud environment.

That includes Logic App workflows as well as the emulator-backed storage, messaging, database, and function-host surfaces.

## Install

```bash
dotnet add package TestFramework.Container.Azure
```

## What It Does

The package plugs into the run builder through `SetEnv(...)` with a `DockerAzureEnvironment`.

That environment:
- starts only the emulator components the timeline actually needs
- rewrites the configured Azure connection settings to the mapped local Docker endpoints
- validates the resolved component graph and binds compatible contracts before startup
- keeps the normal identifier-driven Azure config contract intact
- can inspect Docker-hosted Logic App definitions to distinguish stateful workflows from stateless ones

Those placeholder config entries can be registered directly by the test project, or owned by shared component classes when a reusable test stack wants each component to describe itself completely. The environment treats those configs as identifier registrations plus logical placeholders, then rewrites the runtime endpoints from the activated component graph.

The timeline still reads like a normal TestFramework timeline. The environment is the switch that makes the run container-backed.

For Logic Apps, follow the same rule as the Azure package:
- stateful workflows use `Call()` and may be paired with `RunCompleted(...)`
- stateless workflows use `CallAndCapture()` and assert against the direct callback result

If Docker knows a workflow is stateless from its `workflow.json`, `RunCompleted(...)` fails fast instead of polling a run-history path that will never exist.

## Prerequisites

- Docker Desktop or another compatible Docker engine must be running
- the test project must already reference the normal Azure config identifiers it uses in the timeline
- Service Bus scenarios need a valid topology, preferably through `ConfigureServiceBusTopology(...)`

## Golden Sample

```csharp
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using TestFramework.Azure;
using TestFramework.Azure.Configuration;
using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Azure.Extensions;
using TestFramework.Azure.Identifier;
using TestFramework.Container.Azure;
using TestFramework.Core.Timelines;
using TestFramework.Core.Timelines.Assertions;
using Xunit;

public class ContainerAzureSample
{
	private sealed class SampleCosmos : DockerCosmosDefinition<SampleDocument>
	{
		public override CosmosContainerIdentifier Identifier => "cosmos";
	}

	private sealed record SampleDocument(string Id, string PartitionKey);

	private static readonly Timeline _timeline = Timeline.Create()
		.Trigger(AzureTF.Trigger.IsLive.Cosmos("cosmos", AlivenessLevel.Authenticated)).WithTimeOut(TimeSpan.FromMinutes(2))
		.Build();

	[Fact]
	public async Task Timeline_runs_against_container_backed_azure_services()
	{
		ServiceProvider serviceProvider = new ServiceCollection()
			.AddSingleton(ConfigStore<CosmosContainerDbConfig>.Create("cosmos", new CosmosContainerDbConfig
			{
				ConnectionString = "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;",
				DatabaseName = "sample-db",
				ContainerName = "sample-container",
			}))
			.ConfigureCosmosClientOptions(_ => new CosmosClientOptions
			{
				HttpClientFactory = () => new HttpClient(new HttpClientHandler
				{
					ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
				}),
			})
			.BuildServiceProvider();

		TimelineRun run = await _timeline
			.SetupRun(serviceProvider)
			.SetEnv(DockerAzureEnvironment.For<SampleCosmos>())
			.RunAsync();

		run.EnsureRanToCompletion();
		run.Should().NotHaveLoggedAnyErrors();
		Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.CosmosDbComponentId));
	}
}
```

Why this is the default shape:
- the timeline stays consumer-first and looks like a normal Azure test
- `SetEnv(DockerAzureEnvironment.For<...>())` makes the root component explicit while dependent components stay with the component type that needs them
- assertions focus on the run result and the created environment components

## Definition-Based Composition

Prefer named definition classes for new code. They keep the local infrastructure shape visible in code, make shared test setup easy to reuse, and stay compile-verifiable.
Treat each component class as the owner of its own identifier and structure. If a reusable stack needs fixed placeholder config, let the component class own that registration too.

The current composition model is single-source-of-truth based:
- each definition owns one realized identity
- dependency edges are explicit `ComponentDependency` values
- compatible reuse goes through typed contracts
- Function App resource bindings are derived from the Function App definition
- `Include<TDefinition>()` makes a definition available, but does not force activation on its own

In shared test helpers, that usually means a small project-local base class that couples the definition to its local defaults, for example a storage/cosmos component with fixed names or a Service Bus component that exposes one logical `Endpoint` next to its identifier and topology.

## Function App Pattern

Function Apps are first-class definitions. The Function App definition is the single place where the app declares which resources it uses and which app settings should be materialized from those resources.

```csharp
public sealed class MainStorage : DockerStorageDefinition
{
	public override StorageAccountIdentifier Identifier => "MainStorage";
}

public sealed class MainDb : DockerCosmosDefinition<SampleDocument>
{
	public override CosmosContainerIdentifier Identifier => "MainDb";
}

public sealed class ProcessingReply : DockerServiceBusDefinition
{
	public override ServiceBusIdentifier Identifier => "ProcessingReply";

	protected override DockerServiceBusEndpoint? Endpoint
		=> DockerServiceBusEndpoint.Topic("processing-reply");
}

public sealed class DefaultFunctionApp : DockerFunctionAppDefinition<AnalysisProcessor>
{
	public override FunctionAppIdentifier Identifier => "Default";

	protected override void Configure(DockerFunctionAppBuilder builder)
	{
		builder
			.UseStorage<MainStorage>()
			.UseCosmos<MainDb>()
			.UseServiceBusReply<ProcessingReply>()
			.WithAppSetting("FeatureFlags__VerboseStartup", "true");
	}
}
```

The builder call does all of the relevant work in one place:
- declares graph dependencies
- records runtime resource bindings
- contributes Service Bus topology requirements when needed
- keeps extra app settings as launch metadata only

## Contracts And Reuse

Use contracts when multiple components of the same broad kind exist and you need an explicit semantic match instead of only type identity.

```csharp
public sealed class ReplyBus : DockerServiceBusDefinition
{
	public override ServiceBusIdentifier Identifier => "bus";

	protected override void ConfigureContracts(DockerAzureContractBuilder contracts)
	{
		contracts.Provide(new ServiceBusEndpointContract(
			ContractKey: "reply",
			ServiceBusIdentifier: Identifier,
			EndpointKind: ServiceBusEndpointKind.Queue,
			EntityName: "processing-reply"));
	}
}

public sealed class ReplyConsumer : DockerFunctionAppDefinition<ReplyFunctions>
{
	public override FunctionAppIdentifier Identifier => "func";

	protected override void ConfigureDependencies(DockerAzureDependencyBuilder dependencies)
	{
		dependencies.Include<ReplyBus>();
	}

	protected override void ConfigureContracts(DockerAzureContractBuilder contracts)
	{
		contracts.Require(new ServiceBusEndpointContract(
			ContractKey: "reply",
			ServiceBusIdentifier: "bus",
			EndpointKind: ServiceBusEndpointKind.Queue,
			EntityName: "processing-reply"));
	}
}
```

This is the preferred way to prevent overlap when a test stack exposes multiple blob containers, Cosmos containers, SQL databases, or Service Bus endpoints.

## Typical Pattern

1. Register the Azure config stores your timeline identifiers use, either directly in the test project or through shared component-owned registration helpers.
2. Configure client options that emulators need, such as Cosmos gateway mode or certificate bypass.
3. Build the timeline the same way you would for a normal Azure test.
4. Root the environment with `DockerAzureEnvironment.For<TRootDefinition>()` and chain `.Include<TDefinition>()` only for extra available definitions such as infrastructure overrides or optional shared providers.
5. Let artifacts, environment requirements, dependency traversal, and contract bindings activate the concrete resources that are actually needed for the run.
6. Assert on `TimelineRun`, artifacts, and `EnvironmentContext`.

## Smoke Tests

The normal Container.Azure test project already includes the end-to-end smoke path as part of the full suite:

```powershell
dotnet test .\UnitTests\TestFramework.Container.Azure.Tests\TestFramework.Container.Azure.Tests.csproj -c Release
```

## Documentation Map

- `TestFramework.Container.Azure`: container-backed Azure runtime integration
- `DockerAzureEnvironment`: main environment provider
- `DockerAzureDefinition` types plus `DockerAzureEnvironment.For<TDefinition>()` / `.Include<TDefinition>()`: preferred public composition model
- `TestFramework.Container.Azure/Documentation/Architecture.md`: architecture note for the single-source-of-truth component graph and activation model
