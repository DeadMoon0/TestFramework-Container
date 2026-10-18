<identity>
    <package>TestFramework.Container</package>
    <role>addon-skill</role>
</identity>

<objective>
    Explain how TestFramework timelines run against Docker-backed local infrastructure, including what the user must configure, what the environment resolves automatically, and how container-backed tests differ from tests against already-running systems.
    Two environments exist: DockerAzureEnvironment for Azure emulators, and DockerWebEnvironment (TestFramework.Container.Web) for an application, its SQL Server and its stubbed dependencies.
</objective>

<package_scope>
    Covers DockerAzureEnvironment, DockerAzureDefinition-based composition, Docker-backed Azurite, Cosmos emulator, SQL Server, Service Bus emulator, environment component resolution, endpoints published on the run, and smoke-test usage patterns.
    Covers DockerWebEnvironment: DockerSqlDefinition with model-derived schema and reset modes, DockerApiDefinition with a declared ContainerSource, StubDefinition served from a container, DockerSiteDefinition serving a static frontend (any SPA build or plain pages) from nginx with declared proxy routes and generated runtime config, and the two addresses every resource has.
    Covers the shared building blocks: ContainerSource and its plan, the three build strategies, the offline package feed, readiness waits, log capture and Docker host normalisation.
</package_scope>

<key_concepts>
    TestFramework.Container does not replace the Core timeline model. It plugs into the run through SetEnv(...) with DockerAzureEnvironment.
    The environment is created in pre-setup before the main timeline steps run.
    The package keeps the normal identifier-driven Azure config contract: configuration is loaded with LoadDockerAzureConfig(), and each identifier the timeline uses gets its names (blob container, database, queue) from a configuration entry or from its definition.
    The environment starts local Docker infrastructure only for the Azure resources actually required by the timeline artifacts and environment requirements.
    After the containers are ready, each component publishes its mapped endpoint on the run; steps read it with context.Configured<T>(identifier), so the normal Azure runtime uses the live container endpoints. Nothing a person declared is rewritten.
    For Cosmos, the environment also infers the partition key path from the typed artifact model and deploys schema before item setup begins.
    An application's source is declared, not discovered. ContainerSource.Project(path) names a project; MSBuild is then asked what it is - target framework, assembly name, web SDK, project references - so nothing depends on where an assembly was loaded from or on the test project referencing the application.
    Every source resolves to a ContainerSourcePlan that is logged before anything is built or started, and values that were derived rather than declared say which. Planning has no side effects, so what a run would do is answerable without Docker.
    A project that targets several frameworks is a hard error, not a silent pick: adding a framework must not quietly change what a test runs.
    Three build strategies exist. BuiltAsImage lets the SDK build the image and is the default. BuiltOnHost publishes to a temporary directory. BuiltInContainer builds inside Docker and leaves no build output on the host.
    An in-container build needs no feed credentials: the host restores with the configuration that already works, and the resolved packages are handed over as a pre-populated cache with every NuGet source cleared. It requires the target framework generation to match the SDK on the machine, because which packages count as framework-provided is SDK-version-dependent.
    Every container has two addresses. The test process uses the host-mapped port; another container uses the network alias. UseSql and UseStub always inject the network form, so that cannot be got backwards.
    Databases are reused across runs and have reset modes; applications and stubs are per-run, because there is no reset mode for a stale binary or a stale request log.
</key_concepts>

<best_practices>
    Treat the environment as the only switch for local container-backed infrastructure. Do not mix manual emulator bootstrapping with DockerAzureEnvironment in the same run.
    Keep the timeline explicit and readable. Let SetEnv(...) be the one visible signal that the run is container-backed.
    Still declare every identifier the timeline uses. The environment publishes addresses for them; it does not invent identifiers for you.
    Leave addresses out of the test code and let the components publish the mapped ports on the run.
    Prefer DockerAzureDefinition classes plus DockerAzureEnvironment.For<TComponent>() and .Include<TComponent>() for new code.
    Load configuration with LoadDockerAzureConfig(): it points the Cosmos client at the emulator through Gateway mode and accepts its development certificate. Do not hand-configure Cosmos client options for the emulator.
    Keep Service Bus topology configuration explicit on the owning definition, preferably through ConfigureServiceBusTopology(...). Use ServiceBusTopologyConfigPath only when a shared external JSON file is genuinely the better fit.
    Prefer one project-level helper that loads configuration with LoadDockerAzureConfig() and applies DockerAzureEnvironment consistently.
    Prefer run assertions and environment assertions on TimelineRun over ad-hoc low-level debugging output.
    Declare an application with ContainerSource.Project(...) rather than a marker type; it needs no reference from the test project to the application and no inference from a loaded assembly.
    Read the source plan in the run log first when a container misbehaves. It names the project, the framework, the images and everything that was derived.
    Choose a build strategy explicitly when the default does not fit: BuiltInContainer for a clean host, BuiltOnHost for speed, Image when a pipeline already produced one.
    Generate a fixture schema from models only for a database the test owns; mirror a real schema with a script when it is owned elsewhere.
    Assert that a stub received no unmatched calls; it is the only way a call to an undeclared endpoint becomes visible.
</best_practices>

<api_hints>
    Important APIs and shapes from the package:
    - DockerAzureEnvironment.For<TComponent>()
    - environment.Include<TComponent>()
    - runBuilder.SetEnv(environment)
    - DockerAzureDefinition
    - DockerAzureInfrastructureDefinition
    - DockerStorageAccountDefinition
    - DockerCosmosDbDefinition<T>
    - DockerServiceBusDefinition
    - DockerFunctionAppDefinition with a declared ContainerSource Source (project sources publish on the host: .BuiltOnHost())
    - DockerWebEnvironment.For<TDefinition>().Include<TDefinition>().IncludeStub<TStub>()
    - DockerWebEnvironment.UseSqlImage|UseSqlPassword|UseSqlMemoryLimit|UseStubImage(...)
    - DockerSqlDefinition with DockerSqlBuilder: .WithDatabase(...), .WithSchemaFromModels<T...>(), .WithSchemaScript(...), .WithResetScript(...), .WithResetMode(SqlResetMode.None|RunResetScript|RecreateDatabase)
    - DockerApiDefinition with ContainerSource Source and DockerApiBuilder: .WithEnvironmentName(...), .WithHealthPath(...)|.WithoutHealthCheck(), .WithSetting(...), .WithEnvironmentVariable(...), .UseSql<T>(settingPath), .UseStub<T>(settingPath)
    - DockerSiteDefinition with SiteSource Source and DockerSiteBuilder: .WithImage(...)|.WithPort(...)|.WithReadinessPath(...)|.WithReadinessTimeout(...)|.WithoutSpaFallback(), .ProxyApi<T>(path, stripPrefix)|.ProxyApi(id, path)|.ProxyStub<T>(path), .WithNginxServerDirective(...), .ConfigJsonApi<T>(jsonPath)|.ConfigJsonApi(id, jsonPath)|.ConfigJsonStub<T>(jsonPath)|.WithConfigJsonValue(...)|.WithConfigJsonPath(...), .WithConfigFile(path, ctx => ...) with ctx.Addresses.ApiBaseUrl(id) and ctx.MergeJson((path, value))
    - SiteSource.Image|Directory|NpmProject(.BuiltOnHost()|.BuiltInContainer(nodeImage)|.WithBuildScript(...)|.WithDistPath(...)|.WithoutInstall()|.WithBuildTimeout(...))|ContainerBuild(dir, image, command).WithDistPath(...)
    - ContainerSource.Image|Project|Directory; project modifiers .BuiltAsImage()|.BuiltOnHost()|.BuiltInContainer(), .WithTargetFramework(...), .WithConfiguration(...), .WithRuntimeImage(...), .WithSdkImage(...), .WithContext(...)
    - ContainerSourceResolver.PlanAsync(source, ct) -> ContainerSourcePlan; plan.ToLogLines(identifier)
    - ContainerImageBuilder.BuildAsync(plan, identifier, logger, ct)
    - State: SqlServerComponentState.GetRequiredDatabase(id), ApiComponentState.GetRequiredApi(id), StubComponentState.GetRequiredStub(id), SiteComponentState.GetRequiredSite(id) with BaseUrl, NginxConfig and GeneratedFiles
    - Network aliases on the environment's network: sqlserver, api-{id}, stub-{id}, site-{id}; nginx proxy routes target api-{id}/stub-{id}
    - A browser step for an application with no address of its own requires the site of the same name, so the site identifier IS the web-app identifier; the environment starts the site because it declared it
    - ContainerEndpoints.HostEndpoint|NetworkEndpoint|HostSqlConnectionString|NetworkSqlConnectionString
    - ContainerReadiness.WaitForHttpAsync|WaitForHttpAnswerAsync|WaitForSqlAsync|WaitForSqlStatementAsync
    - ContainerOutputResolver.ResolveTargetFramework
    - ProjectQuery.ReadAsync|ResolveCommonRoot, OfflineFeed.CreateAsync, DockerfileGenerator, ContainerDockerHost.EnsureConfigured
    Component identifiers surfaced by the environment:
    - DockerAzureEnvironment.AzuriteComponentId
    - DockerAzureEnvironment.CosmosDbComponentId
    - DockerAzureEnvironment.MsSqlComponentId
    - DockerAzureEnvironment.ServiceBusComponentId
    - DockerAzureEnvironment.NetworkComponentId

    Operational hint:
    The environment resolves components from artifacts and IHasEnvironmentRequirements, then creates them in pre-setup before main-stage artifact setup starts.
</api_hints>

<runtime_behavior>
    Important runtime facts:
    - The Core run builder allows only one environment for a timeline run.
    - DockerAzureEnvironment maps Azure artifact/reference kinds to container-backed environment components.
    - Azurite is used for blob and table-backed storage artifacts.
    - CosmosDbEnvComponent starts the Linux Cosmos emulator, waits for gateway readiness, publishes the Cosmos endpoint on the run, and deploys database/container schema for the used Cosmos identifiers.
    - MsSqlEnvComponent starts SQL Server, waits for a successful query, then publishes the SQL connection on the run.
    - ServiceBusEnvComponent depends on the Docker network and SQL Server, materializes the configured Service Bus topology, starts the Service Bus emulator, validates namespace availability, then publishes the Service Bus connection on the run.
    - Environment component runtime state is kept in the environment context and deconstructed in cleanup.
    - Function App definitions contribute dependency edges and runtime bindings into the validated component graph early enough that emulator setup can happen before startup
</runtime_behavior>

<validation_guidance>
    Validation posture the agent should preserve:
    - normal unit coverage is expected on every change
    - Docker-backed smoke coverage should stay part of the normal Container.Azure test project unless the repo explicitly reintroduces opt-in gating
    - when the user asks for Function App hosting confidence, prefer the real smoke path over a purely mocked substitute

    Documentation is thinner than the runtime sophistication.
    When helping users, explain prerequisites, topology ownership, and which names each identifier needs explicitly instead of assuming the README already did it.
</validation_guidance>

<config_model>
    Important configuration ideas:
    - Each identifier gets its names (blob container, database, container, entity) from a configuration entry - loaded with LoadDockerAzureConfig() - or from its definition (BlobContainerName, DatabaseName, ContainerName and the like).
    - The containers publish their addresses while they start, as run resource values that outrank any declared entry.
    - ConnectionStringGuards enforce that the published values point to local Docker endpoints.
    - ServiceBus topology can be supplied fluently through ConfigureServiceBusTopology(...) or via a file path for compatibility.
    - ServiceBusConfigLocator resolves file-backed topology paths either as an absolute path or relative to AppContext.BaseDirectory.
    - Cosmos schema creation uses the model type to resolve the partition key path, so the user does not provide PartitionKeyPath manually.
</config_model>

<user_requirements>
    When the user uses DockerAzureEnvironment, they must:
    - have Docker running and reachable from Testcontainers
    - load configuration with LoadDockerAzureConfig() and declare the Azure identifiers the timeline refers to
    - call SetEnv(...) with DockerAzureEnvironment on the run builder
    - provide a valid Service Bus topology when Service Bus is involved, preferably through ConfigureServiceBusTopology(...)
    - define Cosmos models with id and partitionKey semantics, by property name or JSON-mapped name

    When the user does not use DockerAzureEnvironment, they must:
    - provide real reachable connection strings themselves
    - provision or point to already-running resources and topology outside the framework
    - keep the same identifier contract, because the Azure package still resolves named configs from DI
    - handle local emulator startup and schema availability on their own
</user_requirements>

<project_adaptation>
    Adapting this package to a project:
    - keep one shared helper that loads configuration with LoadDockerAzureConfig() and applies DockerAzureEnvironment when the project wants container-backed execution
    - do not scatter DockerAzureEnvironment construction across many tests when one project-level helper can centralize the policy
    - if a project needs different emulator images or shared topology/image overrides, prefer a DockerAzureInfrastructureDefinition added with `.Include<TInfrastructure>()`
    - if the project mixes container-backed and non-container runs, keep the difference explicit at the SetupRun(...).SetEnv(...) call site
</project_adaptation>

<style_guide>
    Prefer tests where the container-backed nature is obvious from the run setup.
    Keep Docker and emulator details out of the timeline body whenever the environment can model them.
    Prefer one shared service-provider builder that loads configuration with LoadDockerAzureConfig().
    Keep assertions focused on run results and environment presence, not on ad-hoc Docker plumbing.
    Docker-backed smoke tests carry [Trait("Category", "DockerSmoke")] and run with the rest of the suite; a machine without Docker leaves them out with --filter "Category!=DockerSmoke". There is no opt-in flag.
    Keep `SetupRun(...).SetEnv(DockerAzureEnvironment.For<...>()).RunAsync()` examples compact unless component-owned placeholder config is the real point of the sample.
</style_guide>

<sample_patterns>
    Minimal container-backed Azure run pattern:
    - give each identifier its names, in configuration or on its definition
    - load configuration with ConfigInstance.Create().LoadDockerAzureConfig(), which also points the Cosmos client at the emulator
    - build the timeline
    - define named DockerAzureDefinition classes for the concrete components you need
    - let one root component declare its dependent component types
    - call timeline.SetupRun(config).SetEnv(DockerAzureEnvironment.For<TRootComponent>()).RunAsync()
    - assert on run completion, environment components, and relevant artifacts

    Service Bus emulator pattern:
    - declare ServiceBus entries as normal named configuration
    - prefer ConfigureServiceBusTopology(...) on a DockerServiceBusDefinition or DockerAzureInfrastructureDefinition so the topology lives next to the definition that owns it
    - let the environment start SQL + Service Bus emulator and publish their addresses

    Smoke-test pattern:
    - keep the smoke test readable and end-to-end
    - assert that the expected environment components were created in EnvironmentContext

    Cosmos emulator pattern:
    - name the database and container, in configuration or on the DockerCosmosDbDefinition
    - use a typed model whose id and partitionKey can be resolved by the shared resolver
    - rely on env pre-setup to publish the emulator's address and deploy the schema before the item artifact runs
</sample_patterns>

<anti_patterns>
    Avoid:
    - starting Docker containers manually in test code when DockerAzureEnvironment already models the resource
    - hardcoding mapped Docker ports into the test instead of letting the components publish them on the run
    - maintaining a separate manual Cosmos partition key path configuration alongside the model type
    - mixing container-backed setup and direct real-cloud connection strings in the same logical test scenario
    - hiding Service Bus topology ownership so emulator startup requirements become unclear
    - assuming Docker is always available in CI or on developer machines; smoke tests should opt in when they require Docker
    - forgetting that Service Bus emulator depends on SQL Server and a Docker network
    - hand-configuring Cosmos client options for the emulator instead of loading configuration with LoadDockerAzureConfig()
</anti_patterns>

<important_type_map>
    Common type map for discovery and error interpretation:
    - DockerAzureEnvironment: the environment provider that decides which Docker-backed resources to create
    - AzuriteEnvComponent: Docker-backed blob/table storage emulator component
    - CosmosDbEnvComponent: Docker-backed Cosmos emulator component with gateway wait and schema deployment
    - MsSqlEnvComponent: Docker-backed SQL Server component
    - ServiceBusEnvComponent: Docker-backed Service Bus emulator component
    - ConnectionStringGuards: validation helpers that assert published endpoints are local emulator endpoints
    - ServiceBusConfigLocator: resolves file-backed Service Bus topology config paths when the fluent builder is not used

    Discovery heuristics for the agent:
    - If users mention SetEnv, Docker, emulator, Azurite, Service Bus emulator, or Cosmos emulator, inspect TestFramework.Container first.
    - If users say the config looks local but the real port is dynamic, read it from the run with context.Configured<T>(identifier), where the component published it.
    - If a container-backed Cosmos test hangs after gateway readiness, check that configuration was loaded with LoadDockerAzureConfig(), which pins the client to the mapped endpoint.
    - If Service Bus emulator creation fails, inspect SQL dependency and the resolved Service Bus topology source first.
</important_type_map>

<sources>
    TestFramework-Container/README.md
    TestFramework-Container/TestFramework.Container/AzureDocker
    TestFramework-Container/UnitTests/TestFramework.Container.Tests
    TestFramework-Showroom/TestFramework.Showroom.Azure
</sources>

<grounding_files>
    Most important files for expert grounding:
    - TestFramework-Container/TestFramework.Container.Azure/DockerAzureEnvironment.cs
    - TestFramework-Container/TestFramework.Container/AzureDocker/Components/AzuriteEnvComponent.cs
    - TestFramework-Container/TestFramework.Container/AzureDocker/Components/CosmosDbEnvComponent.cs
    - TestFramework-Container/TestFramework.Container/AzureDocker/Components/MsSqlEnvComponent.cs
    - TestFramework-Container/TestFramework.Container/AzureDocker/Components/ServiceBusEnvComponent.cs
    - TestFramework-Container/TestFramework.Container/AzureDocker/ConnectionStringGuards.cs
    - TestFramework-Container/TestFramework.Container/AzureDocker/ServiceBusConfigLocator.cs
    - TestFramework-Container/UnitTests/TestFramework.Container.Tests/DockerAzureEnvironmentTests.cs
    - TestFramework-Container/UnitTests/TestFramework.Container.Tests/DockerAzureEnvironmentSmokeTests.cs
</grounding_files>