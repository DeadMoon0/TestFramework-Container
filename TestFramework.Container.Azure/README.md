# TestFramework.Container.Azure

`TestFramework.Container.Azure` lets a normal TestFramework Azure timeline run against Docker-backed emulator infrastructure.

Use it when you want to keep the normal `TestFramework.Azure` timeline shape, but you want Blob, Table, Cosmos, SQL Server, or Service Bus dependencies to come from local containers instead of a live Azure environment.

## Install

```bash
dotnet add package TestFramework.Container.Azure
```

## What It Adds

The package plugs into the run through `SetEnv(...)` with a `DockerAzureEnvironment`.

That environment:
- starts the required emulator components before the main timeline steps run
- rewrites registered Azure config entries to the mapped local Docker endpoints
- validates the resolved component graph and binds compatible contracts before startup
- keeps the normal identifier-driven Azure config contract intact

The placeholder config can still be registered explicitly by the test project, but definition classes can also own that registration when a shared test stack wants each component to describe its own shape. Those placeholders act as logical identifiers; the runtime endpoints come from the activated component graph.

The timeline itself still looks like a normal TestFramework timeline. The environment is the switch that makes the run container-backed.

## Prerequisites

- Docker Desktop or another compatible Docker engine must be running
- the test project must register the Azure identifiers that the timeline uses
- Service Bus scenarios need a valid topology file path
- Cosmos scenarios often need emulator-specific client options such as certificate bypass

## Golden Sample

```csharp
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using TestFramework.Azure;
using TestFramework.Azure.Configuration;
using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Azure.Extensions;
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
- `SetEnv(DockerAzureEnvironment.For<...>())` makes the root component explicit, while dependent components stay with the component type that needs them
- assertions focus on the run result and the created environment components

## Definition-Based Composition

Use named definition classes when you want the infrastructure shape to be visible in code and easy to share between test projects.
Each class should describe one component. When a component needs other components, declare those dependencies through component types.
If a shared test stack needs fixed placeholder config, let that component class own the registration for its own identifier instead of scattering detached config objects elsewhere.

The current public model is single-source-of-truth based:
- each definition owns exactly one realized identity
- dependencies are explicit graph edges
- contracts describe compatible reuse between providers and consumers
- Function App definitions own their resource bindings
- `Include<TDefinition>()` makes a definition available, but does not mark it as used on its own

```csharp
using FunctionApp;
using TestFramework.Container.Azure;

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
	public override string TopologyConfigPath => Path.Combine("ShowroomAzure", "ServiceBus", "config.json");
}

public sealed class DefaultFunctionApp : DockerFunctionAppDefinition<AnalysisProcessor>
{
	public override FunctionAppIdentifier Identifier => "Default";

	protected override void Configure(DockerFunctionAppBuilder builder)
	{
		builder
			.UseStorage<MainStorage>()
			.UseCosmos<MainDb>()
			.UseServiceBusReply<ProcessingReply>();
	}
}

TimelineRun run = await timeline
	.SetupRun(serviceProvider)
	.SetEnv(DockerAzureEnvironment.For<DefaultFunctionApp>())
	.RunAsync();
```

In that example, including `DefaultFunctionApp` is enough because its `Configure(...)` method already declares `MainStorage`, `MainDb`, and `ProcessingReply` by component type.
The component types are the contract: identifier, dependency graph, and, when useful for a shared setup, the placeholder config they register before the environment rewrites endpoints.

`.UseStorage<TStorage>()` now injects `StorageTableName` by default when the storage config defines `TableContainerName`.
Override `tableNameSettingName` when your Function App uses a different setting name, or pass `null` to suppress table-name injection entirely.

At runtime, the Function App definition is compiled into a descriptor that carries:
- launch metadata such as image and extra app settings
- dependency edges for graph validation and activation
- resource bindings used to derive the actual app settings inside the Function App container

Shared test stacks often wrap that placeholder config into the component itself. A showroom-style helper can look like this:

```csharp
private abstract class SharedCosmosDefinition<TDocument> : DockerCosmosDefinition<TDocument>
{
	protected abstract CosmosContainerDbConfig CreateConfig();

	public void Register(IServiceCollection services)
	{
		services.AddSingleton(ConfigStore<CosmosContainerDbConfig>.Create(Identifier, CreateConfig()));
	}
}

private sealed class MainDb : SharedCosmosDefinition<SampleDocument>
{
	public override CosmosContainerIdentifier Identifier => "MainDb";

	protected override CosmosContainerDbConfig CreateConfig() => new()
	{
		ConnectionString = "AccountEndpoint=https://localhost:8081/...",
		DatabaseName = "BaseDb",
		ContainerName = "Profiles",
	};
}

// During test setup:
new MainDb().Register(services);
```

That keeps the identifier, model shape, and placeholder config together in one type instead of splitting them across unrelated helper fields.

## Contracts And Explicit Reuse

Contracts are the preferred mechanism when a consumer should bind to one compatible provider rather than to "whatever happened to be included".

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

public sealed class ReplyConsumerFunctionApp : DockerFunctionAppDefinition<ReplyConsumer>
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

Contract binding happens before runtime startup, so ambiguous or missing providers fail during resolution instead of later during container startup.

## Typical Pattern

1. Register the Azure config stores that your timeline identifiers use, either directly in the test project or through shared component-owned registration helpers.
2. Configure emulator-specific client options where needed.
3. Build the timeline the same way you would for a normal Azure test.
4. Add the root definition through `DockerAzureEnvironment.For<TRootDefinition>()` and chain `.Include<TDefinition>()` only when you need extra available definitions such as infrastructure overrides.
5. Let artifacts, environment requirements, dependency traversal, and contract bindings decide which concrete resources activate for the run.
6. Assert on `TimelineRun`, artifacts, and `EnvironmentContext`.

## Infrastructure Overrides

Use an infrastructure definition when you need explicit emulator-level overrides.

```csharp
public sealed class CustomInfrastructure : DockerAzureInfrastructureDefinition
{
	public override string? AzuriteImage => "mcr.microsoft.com/azure-storage/azurite:3.35.0";
	public override string? CosmosDbImage => "mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview";
	public override string? MsSqlImage => "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";
	public override string? ServiceBusImage => "mcr.microsoft.com/azure-messaging/servicebus-emulator:latest";
	public override string? MsSqlPassword => "TestFramework_Container1!";
	public override string? ServiceBusTopologyConfigPath => Path.Combine("ShowroomAzure", "ServiceBus", "config.json");
}

TimelineRun run = await timeline
	.SetupRun(serviceProvider)
	.SetEnv(DockerAzureEnvironment.For<DefaultFunctionApp>().Include<CustomInfrastructure>())
	.RunAsync();
```

Typical cases:
- provide a Service Bus topology path or a fluent topology override
- pin emulator images for a shared test stack
- override SQL credentials for a local test environment

You can keep using a JSON file when you want an external emulator config, but Service Bus definitions can also describe the topology fluently:

```csharp
public sealed class OrdersBus : DockerServiceBusDefinition
{
	public override ServiceBusIdentifier Identifier => "orders-bus";

	protected override void ConfigureServiceBusTopology(DockerServiceBusTopologyBuilder builder)
	{
		builder.AddNamespace("sbemulatorns", ns => ns
			.AddTopic("orders", topic => topic.AddSubscription("processor"))
			.AddTopic("orders-reply", topic => topic.AddSubscription("default")));
	}
}
```

## Smoke Tests

The end-to-end smoke path runs as part of the normal Container Azure test project and no longer relies on an external opt-in flag:

```powershell
dotnet test .\UnitTests\TestFramework.Container.Azure.Tests\TestFramework.Container.Azure.Tests.csproj -c Release
```

## Related Packages

- `TestFramework.Azure` for Azure triggers, waits, and artifact types
- `TestFramework.Config` for building the service provider and config stores used by the run
- `TestFramework.Core` for the base timeline model and run assertions

## Further Reading

- [Architecture](./Documentation/Architecture.md) for the single-source-of-truth component model, activation semantics, and Function App runtime binding design