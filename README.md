# TestFramework-Container

`TestFramework.Container.Azure` lets a normal TestFramework timeline run against Docker-backed Azure emulators.

Use it when you want the Azure timeline shape from `TestFramework.Azure`, but you want the backing services to come from local containers instead of a live cloud environment.

## Install

```bash
dotnet add package TestFramework.Container.Azure
```

## What It Does

The package plugs into the run builder through `SetEnv(...)` with a `DockerAzureEnvironment`.

That environment:
- starts only the emulator components the timeline actually needs
- rewrites the configured Azure connection settings to the mapped local Docker endpoints
- keeps the normal identifier-driven Azure config contract intact

Those placeholder config entries can be registered directly by the test project, or owned by shared component classes when a reusable test stack wants each component to describe itself completely.

The timeline still reads like a normal TestFramework timeline. The environment is the switch that makes the run container-backed.

## Prerequisites

- Docker Desktop or another compatible Docker engine must be running
- the test project must already reference the normal Azure config identifiers it uses in the timeline
- Service Bus scenarios need a valid topology file path

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

In shared test helpers, that usually means a small project-local base class that couples the definition to its placeholder config registration, for example a storage/cosmos/service-bus component that exposes a `Register(IServiceCollection ...)` helper next to its identifier and model shape.

## Typical Pattern

1. Register the Azure config stores your timeline identifiers use, either directly in the test project or through shared component-owned registration helpers.
2. Configure client options that emulators need, such as Cosmos gateway mode or certificate bypass.
3. Build the timeline the same way you would for a normal Azure test.
4. Call `SetupRun(serviceProvider).SetEnv(DockerAzureEnvironment.For<TComponent>()).RunAsync()` and chain `.Include<TComponent>()` when you need extra explicit components.
5. Assert on `TimelineRun`, artifacts, and `EnvironmentContext`.

## Smoke Tests

The normal Container.Azure test project already includes the end-to-end smoke path:

```powershell
dotnet test .\UnitTests\TestFramework.Container.Azure.Tests\TestFramework.Container.Azure.Tests.csproj -c Release
```

## Documentation Map

- `TestFramework.Container.Azure`: container-backed Azure runtime integration
- `DockerAzureEnvironment`: main environment provider
- `DockerAzureDefinition` types plus `DockerAzureEnvironment.For<TComponent>()` / `.Include<TComponent>()`: preferred public composition model
