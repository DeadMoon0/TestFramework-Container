# TestFramework.Container.Azure Architecture

This document is for engineers who need to compose or extend container-backed Azure test environments.
After reading it, the expected outcome is that you can model a test stack with `DockerAzureDefinition` types, predict which resources will activate, and understand how Function App settings are derived at runtime.

## Design Goal

The package uses one definition graph as the single source of truth for three concerns that used to drift apart:

- what components exist
- how those components depend on each other
- how runtime settings are produced for hosted Function Apps

The design aims to keep the public authoring model small while making activation and reuse explicit.

## The Core Model

Each container-backed Azure resource is described by a `DockerAzureDefinition`.
Concrete definitions own their own identifier and optional dependency and contract declarations.

Minimal examples:

```csharp
public sealed class MainStorage : DockerStorageDefinition
{
	public override StorageAccountIdentifier Identifier => "MainStorage";
}

public sealed class MainDb : DockerCosmosDefinition<ProfileDocument>
{
	public override CosmosContainerIdentifier Identifier => "MainDb";
}

public sealed class MainBus : DockerServiceBusDefinition
{
	public override ServiceBusIdentifier Identifier => "ProcessingReply";
}
```

Examples:

- `DockerStorageDefinition`
- `DockerCosmosDefinition<TDocument>`
- `DockerSqlDefinition`
- `DockerServiceBusDefinition`
- `DockerFunctionAppDefinition<TFunctionApp>`
- `DockerAzureInfrastructureDefinition`

The important rule is: one definition describes one realized component identity.

That identity is the unit used for:

- graph validation
- activation
- exclusive-sharing checks
- contract binding

## Explicit Dependency Graph

Dependencies are declared as explicit `ComponentDependency` edges.
They are not inferred from random runtime state.

Two authoring styles feed the same graph:

1. `ConfigureDependencies(DockerAzureDependencyBuilder)` for structural dependencies.
2. `Configure(DockerFunctionAppBuilder)` for Function App-owned resource usage.

That means a Function App builder call such as `UseStorage<TStorage>()` is not just a convenience method.
It creates a real dependency edge that the graph validator and activation pass can see.

## Function Apps As First-Class Components

Function Apps are now modeled as definitions, not as resource-identifier bags.

A Function App definition owns:

- the Function App identifier
- the hosted function type
- the resource dependencies the app needs
- the resource bindings that become app settings
- any extra launch metadata such as image overrides or additional app settings

The registration object still exists, but its role is intentionally smaller.
`DockerFunctionAppRegistration` is launch metadata only.
It no longer owns the main dependency or resource-binding story.

Typical Function App definition:

```csharp
public sealed class DefaultFunctionApp : DockerFunctionAppDefinition<AnalysisProcessor>
{
	public override FunctionAppIdentifier Identifier => "Default";

	protected override void Configure(DockerFunctionAppBuilder builder)
	{
		builder
			.UseStorage<MainStorage>()
			.UseCosmos<MainDb>()
			.UseServiceBusReply<MainBus>()
			.WithAppSetting("FeatureFlags__VerboseStartup", "true");
	}
}
```

That single definition tells the system:

- which Function App should run
- which resources must be available
- which runtime bindings should be materialized into app settings
- which extra launch settings should be added

## Descriptor Compilation

At definition-registration time, each `DockerFunctionAppDefinition` is compiled into a descriptor.
That descriptor carries:

- the registration
- the dependency edges declared by the builder
- Service Bus topology path contributions
- the resource bindings used by the runtime component

This is the handoff point between authoring and runtime.

In other words:

- definitions are the authoring model
- descriptors are the runtime-ready snapshot for hosted Function Apps

## Contracts Solve Reuse And Disambiguation

Type identity is enough for many stacks, but not all.
When multiple compatible providers can exist, contracts make the intended match explicit.

The package currently supports typed Azure contracts for:

- blob containers
- Cosmos containers
- SQL databases
- Service Bus endpoints

Contracts are used for two things:

- expressing what a component provides
- expressing what a component requires

The contract-binding pass runs before startup and enforces that every requirement resolves to exactly one compatible provider.

This prevents two common failures:

- no provider exists for the requested logical slot
- multiple providers match and the runtime would otherwise choose ambiguously

Provider and consumer example:

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

## Activation Model

`Include<TDefinition>()` makes a definition available to the environment.
It does not, by itself, mark that definition as used.

Concrete activation now starts from real roots:

- artifacts that imply resource identifiers
- explicit environment requirements
- explicitly rooted definitions such as `DockerAzureEnvironment.For<TRootDefinition>()`
- providers reached through dependency traversal
- providers reached through successful contract bindings

This is the critical semantic rule of the new model:

Definitions describe availability.
Roots, traversal, and bindings decide actual activation.

## Runtime Rewriting

Users still register named Azure config entries through the normal config-store path.
Those initial config values act as logical placeholders.

When the environment resolves the activated graph, runtime components:

- start the required Docker-backed emulators
- discover mapped endpoints
- rewrite the placeholder connection values to the container-backed endpoints
- keep the logical identifier contract unchanged for the timeline code

This lets timeline code stay close to the non-container Azure shape while the environment swaps in Docker-backed infrastructure underneath it.

## Function App Runtime Settings

Hosted Function App settings are built from descriptor resource bindings.
The runtime component reads each binding and maps it to concrete settings such as:

- storage connection strings
- host storage settings
- Cosmos connection, database, and container settings
- Service Bus connection strings and entity names

This is why the Function App definition is the single source of truth.
The user declares resource usage once, and the runtime derives the final settings from that declaration.

## Infrastructure Overrides

`DockerAzureInfrastructureDefinition` exists for stack-wide emulator policy, not workload behavior.

Use it when you need to override things such as:

- Azurite image
- Cosmos emulator image
- SQL Server image
- Service Bus emulator image
- SQL password
- Service Bus topology config path

Keeping these overrides in infrastructure definitions avoids mixing stack-level policy into workload definitions.

## Recommended Composition Pattern

For new code, the intended composition flow is:

1. Create small named definitions for each resource.
2. Let each definition own its identifier.
3. Use `DockerFunctionAppDefinition<TFunctionApp>` as the root when a hosted Function App is the main workload.
4. Use contracts when multiple compatible providers could otherwise overlap.
5. Add infrastructure overrides only when the stack needs them.
6. Let artifacts, requirements, dependency traversal, and contract binding decide what activates.

This keeps the public model predictable and avoids hidden include-time activation.

End-to-end composition example:

```csharp
DockerAzureEnvironment environment = new DockerAzureEnvironment()
	.Include<MainStorage>()
	.Include<MainDb>()
	.Include<MainBus>()
	.Include<DefaultFunctionApp>();

TimelineRun run = await timeline
	.SetupRun(serviceProvider)
	.SetEnv(DockerAzureEnvironment.For<DefaultFunctionApp>())
	.RunAsync();
```

The important distinction is that `Include<TDefinition>()` makes definitions available, while `For<DefaultFunctionApp>()` gives the environment a concrete root from which activation can expand.

## Practical Mental Model

If you need one sentence to carry into implementation work, use this one:

`TestFramework.Container.Azure` is a validated, contract-bound component graph where definitions describe availability and identity, while rooted traversal decides what actually runs.