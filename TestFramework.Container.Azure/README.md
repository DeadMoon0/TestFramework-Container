# TestFramework.Container.Azure

`TestFramework.Container.Azure` lets a normal TestFramework Azure timeline run against Docker-backed emulator infrastructure.

Use it when you want to keep the normal `TestFramework.Azure` timeline shape, but you want Blob, Table, Cosmos, SQL Server, or Service Bus dependencies to come from local containers instead of a live Azure environment.

## Install

```bash
dotnet add package TestFramework.Container.Azure
```

## What It Adds

The package plugs into the run through `SetEnv(new DockerAzureEnvironment(...))`.

That environment:
- starts the required emulator components before the main timeline steps run
- rewrites registered Azure config entries to the mapped local Docker endpoints
- keeps the normal identifier-driven Azure config contract intact

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
			.SetEnv(new DockerAzureEnvironment())
			.RunAsync();

		run.EnsureRanToCompletion();
		run.Should().NotHaveLoggedAnyErrors();
		Assert.True(run.EnvironmentContext.Contains(DockerAzureEnvironment.CosmosDbComponentId));
	}
}
```

Why this is the default shape:
- the timeline stays consumer-first and looks like a normal Azure test
- `SetEnv(new DockerAzureEnvironment())` is the only visible switch that makes the run container-backed
- assertions focus on the run result and the created environment components

## Typical Pattern

1. Register the normal Azure config stores that your timeline identifiers use.
2. Configure emulator-specific client options where needed.
3. Build the timeline the same way you would for a normal Azure test.
4. Call `SetupRun(serviceProvider).SetEnv(new DockerAzureEnvironment()).RunAsync()`.
5. Assert on `TimelineRun`, artifacts, and `EnvironmentContext`.

## When To Use Options

Use `DockerAzureEnvironmentOptions` when the timeline does not expose enough information for the environment to infer the required components.

Typical cases:
- force a specific Cosmos, Storage, SQL, or Service Bus identifier
- provide `ServiceBusTopologyConfigPath`
- register Docker-hosted Function Apps

## Smoke Tests

The end-to-end smoke path is intentionally opt-in:

```powershell
$env:TESTFRAMEWORK_CONTAINER_SMOKE = '1'
dotnet test .\UnitTests\TestFramework.Container.Azure.Tests\TestFramework.Container.Azure.Tests.csproj -c Release
```

## Related Packages

- `TestFramework.Azure` for Azure triggers, waits, and artifact types
- `TestFramework.Config` for building the service provider and config stores used by the run
- `TestFramework.Core` for the base timeline model and run assertions