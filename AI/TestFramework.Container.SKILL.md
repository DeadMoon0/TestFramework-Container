<identity>
    <package>TestFramework.Container</package>
    <role>addon-skill</role>
</identity>

<objective>
    Explain how TestFramework timelines run against Docker-backed local infrastructure through DockerAzureEnvironment, including what the user must configure, what the environment resolves automatically, and how container-backed Azure tests differ from non-container Azure tests.
</objective>

<package_scope>
    Covers DockerAzureEnvironment, DockerAzureDefinition-based composition, Docker-backed Azurite, Cosmos emulator, SQL Server, Service Bus emulator, environment component resolution, connection-string rewriting, and smoke-test usage patterns.
</package_scope>

<key_concepts>
    TestFramework.Container does not replace the Core timeline model. It plugs into the run through SetEnv(...) with DockerAzureEnvironment.
    The environment is created in pre-setup before the main timeline steps run.
    The package keeps the normal identifier-driven Azure config contract. Users still register ConfigStore entries such as storage, cosmos, sql, or bus.
    The environment starts local Docker infrastructure only for the Azure resources actually required by the timeline artifacts and environment requirements.
    After the containers are ready, the environment rewrites the registered ConfigStore entries to the mapped Docker endpoints so the normal Azure runtime uses the live container endpoints.
    For Cosmos, the environment also infers the partition key path from the typed artifact model and deploys schema before item setup begins.
</key_concepts>

<best_practices>
    Treat the environment as the only switch for local container-backed infrastructure. Do not mix manual emulator bootstrapping with DockerAzureEnvironment in the same run.
    Keep the timeline explicit and readable. Let SetEnv(...) be the one visible signal that the run is container-backed.
    Still register typed config stores up front. The environment mutates those stores at runtime; it does not invent identifiers for you.
    Prefer logical defaults in the test code and let DockerAzureEnvironment replace the connection strings with mapped ports.
    Prefer DockerAzureDefinition classes plus DockerAzureEnvironment.For<TComponent>() and .Include<TComponent>() for new code.
    For Cosmos emulator clients that run through a mapped Docker port, prefer Gateway mode and certificate bypass in test-only code, and pin the client to the configured endpoint when the SDK would otherwise follow the emulator's advertised internal endpoint.
    Keep Service Bus topology configuration explicit through ServiceBusTopologyConfigPath instead of burying it in helper code.
    Prefer one project-level helper that wires config stores and DockerAzureEnvironment consistently.
    Prefer run assertions and environment assertions on TimelineRun over ad-hoc low-level debugging output.
</best_practices>

<api_hints>
    Important APIs and shapes from the package:
    - DockerAzureEnvironment.For<TComponent>()
    - environment.Include<TComponent>()
    - runBuilder.SetEnv(environment)
    - DockerAzureDefinition
    - DockerAzureInfrastructureDefinition
    - DockerStorageDefinition
    - DockerCosmosDefinition<T>
    - DockerServiceBusDefinition
    - DockerFunctionAppDefinition<T>
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
    - CosmosDbEnvComponent starts the Linux Cosmos emulator, waits for gateway readiness, rewrites the Cosmos config store, and deploys database/container schema for the used Cosmos identifiers.
    - MsSqlEnvComponent starts SQL Server, waits for a successful query, then rewrites the SQL config store.
    - ServiceBusEnvComponent depends on the Docker network and SQL Server, loads the topology config file, starts the Service Bus emulator, validates namespace availability, then rewrites the Service Bus config store.
    - Environment component runtime state is kept in the environment context and deconstructed in cleanup.
    - Function App definitions contribute dependency edges and runtime bindings into the validated component graph early enough that emulator setup can happen before startup
</runtime_behavior>

<validation_guidance>
    Validation posture the agent should preserve:
    - normal unit coverage is expected on every change
    - Docker-backed smoke coverage should stay part of the normal Container.Azure test project unless the repo explicitly reintroduces opt-in gating
    - when the user asks for Function App hosting confidence, prefer the real smoke path over a purely mocked substitute

    Documentation is thinner than the runtime sophistication.
    When helping users, explain prerequisites, topology-path requirements, and config-store expectations explicitly instead of assuming the README already did it.
</validation_guidance>

<config_model>
    Important configuration ideas:
    - Users still provide named Azure config entries through ConfigStore<TConfig> or the normal Azure config-loading path.
    - Those initial config entries act as logical placeholders and identifier registrations.
    - During pre-setup, the environment replaces the placeholder connection strings with real mapped localhost endpoints from Docker.
    - ConnectionStringGuards enforce that the rewritten values point to local Docker endpoints.
    - ServiceBusConfigLocator resolves the topology file either as an absolute path or relative to AppContext.BaseDirectory.
    - Cosmos schema creation uses the model type to resolve the partition key path, so the user does not provide PartitionKeyPath manually.
</config_model>

<user_requirements>
    When the user uses DockerAzureEnvironment, they must:
    - have Docker running and reachable from Testcontainers
    - register the normal Azure identifiers and typed config stores the timeline refers to
    - call SetEnv(...) with DockerAzureEnvironment on the run builder
    - provide a valid Service Bus topology config path when Service Bus is involved
    - define Cosmos models with id and partitionKey semantics, by property name or JSON-mapped name

    When the user does not use DockerAzureEnvironment, they must:
    - provide real reachable connection strings themselves
    - provision or point to already-running resources and topology outside the framework
    - keep the same identifier contract, because the Azure package still resolves named configs from DI
    - handle local emulator startup and schema availability on their own
</user_requirements>

<project_adaptation>
    Adapting this package to a project:
    - keep one shared helper that registers the Azure config stores and applies DockerAzureEnvironment when the project wants container-backed execution
    - do not scatter DockerAzureEnvironment construction across many tests when one project-level helper can centralize the policy
    - if a project needs different emulator images or shared topology/image overrides, prefer a DockerAzureInfrastructureDefinition added with `.Include<TInfrastructure>()`
    - if the project mixes container-backed and non-container runs, keep the difference explicit at the SetupRun(...).SetEnv(...) call site
</project_adaptation>

<style_guide>
    Prefer tests where the container-backed nature is obvious from the run setup.
    Keep Docker and emulator details out of the timeline body whenever the environment can model them.
    Prefer one shared service-provider builder that registers the typed config stores and Cosmos client options for emulator usage.
    Keep assertions focused on run results and environment presence, not on ad-hoc Docker plumbing.
    For smoke tests, gate expensive end-to-end container runs behind an explicit environment variable rather than making the whole suite require Docker.
    Keep `SetupRun(...).SetEnv(DockerAzureEnvironment.For<...>()).RunAsync()` examples compact unless component-owned placeholder config is the real point of the sample.
</style_guide>

<sample_patterns>
    Minimal container-backed Azure run pattern:
    - register ConfigStore<StorageAccountConfig>, ConfigStore<CosmosContainerDbConfig>, ConfigStore<SqlDatabaseConfig>, or ConfigStore<ServiceBusConfig>
    - configure any required Cosmos client options for emulator TLS/gateway behavior
    - build the timeline
    - define named DockerAzureDefinition classes for the concrete components you need
    - let one root component declare its dependent component types
    - call timeline.SetupRun(serviceProvider).SetEnv(DockerAzureEnvironment.For<TRootComponent>()).RunAsync()
    - assert on run completion, environment components, and relevant artifacts

    Service Bus emulator pattern:
    - register ServiceBusConfig entries as normal named configs
    - prefer a DockerServiceBusDefinition or DockerAzureInfrastructureDefinition for topology-path ownership
    - let the environment start SQL + Service Bus emulator and rewrite the connection strings

    Smoke-test pattern:
    - keep the smoke test readable and end-to-end
    - assert that the expected environment components were created in EnvironmentContext

    Cosmos emulator pattern:
    - register a Cosmos config store entry with identifier, database, and container
    - use a typed model whose id and partitionKey can be resolved by the shared resolver
    - configure CosmosClientOptions for Gateway mode and emulator certificate handling when needed
    - rely on env pre-setup to rewrite the connection string and deploy schema before the item artifact runs
</sample_patterns>

<anti_patterns>
    Avoid:
    - starting Docker containers manually in test code when DockerAzureEnvironment already models the resource
    - hardcoding mapped Docker ports into the test instead of letting the environment rewrite config stores
    - maintaining a separate manual Cosmos partition key path configuration alongside the model type
    - mixing container-backed setup and direct real-cloud connection strings in the same logical test scenario
    - hiding the Service Bus topology file dependency so failures become mysterious FileNotFoundExceptions
    - assuming Docker is always available in CI or on developer machines; smoke tests should opt in when they require Docker
    - forgetting that Service Bus emulator depends on SQL Server and a Docker network
    - letting Cosmos SDK clients follow the emulator's advertised internal endpoint when the actual working endpoint is the mapped localhost port
</anti_patterns>

<important_type_map>
    Common type map for discovery and error interpretation:
    - DockerAzureEnvironment: the environment provider that decides which Docker-backed resources to create
    - AzuriteEnvComponent: Docker-backed blob/table storage emulator component
    - CosmosDbEnvComponent: Docker-backed Cosmos emulator component with gateway wait and schema deployment
    - MsSqlEnvComponent: Docker-backed SQL Server component
    - ServiceBusEnvComponent: Docker-backed Service Bus emulator component
    - ConnectionStringGuards: validation helpers that assert rewritten config targets local emulator endpoints
    - ServiceBusConfigLocator: resolves the Service Bus topology config file path

    Discovery heuristics for the agent:
    - If users mention SetEnv, Docker, emulator, Azurite, Service Bus emulator, or Cosmos emulator, inspect TestFramework.Container first.
    - If users say the config looks local but the real port is dynamic, suspect config-store rewriting by DockerAzureEnvironment.
    - If a container-backed Cosmos test hangs after gateway readiness, inspect the client options and whether the SDK is following the emulator's advertised endpoint instead of the mapped endpoint.
    - If Service Bus emulator creation fails, inspect SQL dependency and ServiceBusTopologyConfigPath resolution first.
</important_type_map>

<sources>
    TestFramework-Container/README.md
    TestFramework-Container/TestFramework.Container/AzureDocker
    TestFramework-Container/UnitTests/TestFramework.Container.Tests
    TestFramework-Showroom/TestFramework.Showroom.Azure
</sources>

<grounding_files>
    Most important files for expert grounding:
    - TestFramework-Container/TestFramework.Container/AzureDocker/DockerAzureEnvironment.cs
    - TestFramework-Container/TestFramework.Container/AzureDocker/Components/AzuriteEnvComponent.cs
    - TestFramework-Container/TestFramework.Container/AzureDocker/Components/CosmosDbEnvComponent.cs
    - TestFramework-Container/TestFramework.Container/AzureDocker/Components/MsSqlEnvComponent.cs
    - TestFramework-Container/TestFramework.Container/AzureDocker/Components/ServiceBusEnvComponent.cs
    - TestFramework-Container/TestFramework.Container/AzureDocker/ConnectionStringGuards.cs
    - TestFramework-Container/TestFramework.Container/AzureDocker/ServiceBusConfigLocator.cs
    - TestFramework-Container/UnitTests/TestFramework.Container.Tests/DockerAzureEnvironmentTests.cs
    - TestFramework-Container/UnitTests/TestFramework.Container.Tests/DockerAzureEnvironmentSmokeTests.cs
</grounding_files>