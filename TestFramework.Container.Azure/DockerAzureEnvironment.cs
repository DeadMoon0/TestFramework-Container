using System;
using System.Collections.Generic;
using TestFramework.Azure;
using TestFramework.Azure.DB.CosmosDB;
using TestFramework.Azure.DB.SqlServer;
using TestFramework.Azure.Identifier;
using TestFramework.Azure.StorageAccount.Blob;
using TestFramework.Azure.StorageAccount.Table;
using TestFramework.Core.Environment;
using TestFramework.Core.Environment.Graph;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Azure;

/// <summary>
/// An environment provider that runs Azure resources in Docker containers: Azurite for storage, the Cosmos DB
/// emulator, SQL Server, the Service Bus emulator and Function App hosts, all on one Docker network.
/// </summary>
/// <remarks>
/// What it can start is described by the definitions it includes; a run starts only the containers its artifacts
/// and requirements use.
/// </remarks>
public sealed partial class DockerAzureEnvironment : EnvironmentProviderBase, IRunScopedServiceProviderFactory, IRunScopedEnvironmentFactory, IPersistentEnvironmentStateSink, IResourceNodeSource
{
    /// <summary>
    /// The component that creates the Docker network the containers share.
    /// </summary>
    public static readonly EnvComponentIdentifier NetworkComponentId = "docker-network";

    /// <summary>
    /// The component that starts the Function App host containers.
    /// </summary>
    public static readonly EnvComponentIdentifier FunctionAppComponentId = "functionapp";

    /// <summary>
    /// The component that starts the SQL Server container.
    /// </summary>
    public static readonly EnvComponentIdentifier MsSqlComponentId = "mssql";

    /// <summary>
    /// The component that starts the Azurite container.
    /// </summary>
    public static readonly EnvComponentIdentifier AzuriteComponentId = "azurite";

    /// <summary>
    /// The component that starts the Cosmos DB emulator container.
    /// </summary>
    public static readonly EnvComponentIdentifier CosmosDbComponentId = "cosmos-emulator";

    /// <summary>
    /// The component that starts the Service Bus emulator container.
    /// </summary>
    public static readonly EnvComponentIdentifier ServiceBusComponentId = "servicebus-emulator";

    /// <summary>
    /// The per-run component that empties the emulators before anything is started against them.
    /// </summary>
    public static readonly EnvComponentIdentifier AzureResetComponentId = "azure-reset";

    /// <summary>
    /// The host name other containers on the network reach Azurite by.
    /// </summary>
    public const string AzuriteNetworkAlias = "azurite";

    /// <summary>
    /// The host name other containers on the network reach the Cosmos DB emulator by.
    /// </summary>
    public const string CosmosDbNetworkAlias = "cosmos-emulator";

    /// <summary>
    /// The host name other containers on the network reach the Service Bus emulator by.
    /// </summary>
    public const string ServiceBusNetworkAlias = "servicebus-emulator";
    private static readonly DockerEndpointMap EndpointMapInstance = new();

    private readonly object _reusedGate = new();
    private readonly DockerAzureDefinitionState _definitionState = new();
    private readonly Dictionary<Type, DockerAzureDefinition> _includedDefinitions = [];
    private DockerAzureResolutionSnapshot _lastResolutionSnapshot = DockerAzureResolutionSnapshot.Empty;
    private bool _resolutionSummaryLogged;
    private AzureResetMode _resetMode = AzureResetMode.PurgeDeclaredResources;
    private readonly HashSet<EnvComponentIdentifier> _reusedPersistentComponents = [];
    private readonly HashSet<string> _usedStorageIdentifiers = [];
    private readonly HashSet<string> _usedCosmosIdentifiers = [];
    private readonly HashSet<string> _usedSqlIdentifiers = [];
    private readonly HashSet<string> _usedServiceBusIdentifiers = [];
    private readonly HashSet<string> _usedFunctionAppIdentifiers = [];

    /// <summary>
    /// The storage identifiers this instance's resolution decided the run needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A run resolves on an instance of its own (<see cref="CreateForRun"/>), so after a run read this from
    /// <c>run.Environment</c>; the environment handed to <c>SetEnv</c> takes part in no run and stays empty.
    /// </para>
    /// <para>
    /// Read-only, because <see cref="ResolveComponents"/> clears and refills it. Anything a caller added
    /// by hand would disappear on the next resolution, which is not a thing a public collection should
    /// let you attempt.
    /// </para>
    /// </remarks>
    public IReadOnlyCollection<string> UsedStorageIdentifiers => _usedStorageIdentifiers;

    /// <inheritdoc cref="UsedStorageIdentifiers" />
    public IReadOnlyCollection<string> UsedCosmosIdentifiers => _usedCosmosIdentifiers;

    /// <inheritdoc cref="UsedStorageIdentifiers" />
    public IReadOnlyCollection<string> UsedSqlIdentifiers => _usedSqlIdentifiers;

    /// <inheritdoc cref="UsedStorageIdentifiers" />
    public IReadOnlyCollection<string> UsedServiceBusIdentifiers => _usedServiceBusIdentifiers;

    /// <inheritdoc cref="UsedStorageIdentifiers" />
    public IReadOnlyCollection<string> UsedFunctionAppIdentifiers => _usedFunctionAppIdentifiers;
    internal Dictionary<string, string> CosmosPartitionKeyPaths { get; } = [];

    /// <summary>
    /// Creates an environment with no definitions included. The container components are registered, and storage,
    /// Cosmos and SQL artifacts are mapped to the emulator that serves them.
    /// </summary>
    public DockerAzureEnvironment()
    {
        _definitionNodes = new DefinitionDefaults(_definitionState);

        AddComponent(new Components.DockerNetworkEnvComponent());
        AddComponent(new Components.FunctionAppEnvComponent(this));
        AddComponent(new Components.MsSqlEnvComponent());
        AddComponent(new Components.AzuriteEnvComponent());
        AddComponent(new Components.CosmosDbEnvComponent());
        AddComponent(new Components.ServiceBusEnvComponent());
        AddComponent(new Components.AzureResetEnvComponent(this));

        MapResourceKind(AzureEnvironmentResourceKinds.FunctionApp, FunctionAppComponentId);
        MapResourceKind(AzureEnvironmentResourceKinds.Storage, AzuriteComponentId);
        MapResourceKind(AzureEnvironmentResourceKinds.Cosmos, CosmosDbComponentId);
        MapResourceKind(AzureEnvironmentResourceKinds.Sql, MsSqlComponentId);
        MapResourceKind(AzureEnvironmentResourceKinds.ServiceBus, ServiceBusComponentId);

        MapArtifact<StorageAccountBlobArtifactDescriber>(AzuriteComponentId);
        MapArtifact(typeof(TableStorageEntityArtifactDescriber<>), AzuriteComponentId);
        MapArtifact(typeof(CosmosDbItemArtifactDescriber<>), CosmosDbComponentId);
        MapArtifact(typeof(AzureSqlRowArtifactDescriber<>), MsSqlComponentId);
    }

    /// <summary>
    /// Always <c>true</c>: components whose dependencies are ready are created in parallel.
    /// </summary>
    public override bool SupportsParallelComponentCreation => true;

    /// <summary>
    /// Records that the component was created by an earlier run, so the per-run reset knows there may be emulator
    /// contents to purge. The state itself is not kept.
    /// </summary>
    /// <param name="identifier">The component created by an earlier run.</param>
    /// <param name="state">The component's state; ignored.</param>
    public void SetPersistentState(EnvComponentIdentifier identifier, object? state)
    {
        // Being handed state is the one signal that this component was created by an earlier run. The state
        // itself reaches the run's components through the run, which holds what each of them produced.
        lock (_reusedGate)
            _reusedPersistentComponents.Add(identifier);
    }

    internal DockerEndpointMap GetEndpointMap()
    {
        return EndpointMapInstance;
    }

    internal IReadOnlyCollection<DockerFunctionAppRegistration> GetFunctionAppRegistrations()
    {
        return [.. _definitionState.FunctionApps];
    }

    internal FunctionAppDefinitionDescriptor GetRequiredFunctionAppDescriptor(FunctionAppIdentifier identifier)
    {
        return _definitionState.GetRequiredFunctionAppDescriptor(identifier);
    }

    /// <summary>
    /// Builds the component set a Function App container actually needs, derived from the resource bindings the
    /// Function App declared instead of from a hardcoded list of every emulator this environment can host.
    /// </summary>
    /// <remarks>
    /// This reads resolution state. Before <see cref="ResolveComponents"/> has run there are no resolved Function Apps,
    /// so the result degrades to the network component alone — which is safe because
    /// <c>FunctionAppEnvComponent.CreateAsync</c> early-returns when no Function App resolved.
    /// </remarks>
    internal IReadOnlyList<EnvComponentIdentifier> GetFunctionAppComponentDependencies()
    {
        HashSet<EnvComponentIdentifier> dependencies = [NetworkComponentId];
        foreach (string identifier in UsedFunctionAppIdentifiers)
        {
            foreach (EnvComponentIdentifier component in GetFunctionAppResourceComponents(identifier))
                dependencies.Add(component);
        }

        // An application started against stores a previous run filled would read that run's data before
        // the purge ever got to it.
        if (dependencies.Count > 1)
            dependencies.Add(AzureResetComponentId);

        return [.. dependencies];
    }

    /// <summary>
    /// Maps the resource bindings of a single Function App onto the environment components that back them.
    /// </summary>
    /// <remarks>
    /// MsSql is deliberately absent: the Service Bus component already names it as a dependency and the component
    /// graph walks dependencies transitively.
    /// </remarks>
    internal IReadOnlyCollection<EnvComponentIdentifier> GetFunctionAppResourceComponents(FunctionAppIdentifier identifier)
    {
        if (!_definitionState.TryGetFunctionAppDescriptor(identifier, out FunctionAppDefinitionDescriptor? descriptor) || descriptor is null)
            return [];

        HashSet<EnvComponentIdentifier> components = [];
        foreach (FunctionAppResourceBinding binding in descriptor.ResourceBindings)
        {
            switch (binding.Kind)
            {
                case FunctionAppResourceBindingKind.Storage:
                    components.Add(AzuriteComponentId);
                    break;
                case FunctionAppResourceBindingKind.Cosmos:
                    components.Add(CosmosDbComponentId);
                    break;
                case FunctionAppResourceBindingKind.ServiceBusTrigger:
                case FunctionAppResourceBindingKind.ServiceBusReply:
                    components.Add(ServiceBusComponentId);
                    break;
            }
        }

        return components;
    }

    /// <summary>
    /// The emulators the per-run reset has to wait for before it can purge them.
    /// </summary>
    /// <remarks>
    /// Only the ones that resolved. Naming all four unconditionally would start all four, which is the
    /// cost this environment went to some trouble to stop paying.
    /// </remarks>
    internal IReadOnlyList<EnvComponentIdentifier> GetResetComponentDependencies()
    {
        List<EnvComponentIdentifier> dependencies = [NetworkComponentId];
        if (UsedStorageIdentifiers.Count > 0)
            dependencies.Add(AzuriteComponentId);
        if (UsedCosmosIdentifiers.Count > 0)
            dependencies.Add(CosmosDbComponentId);
        if (UsedSqlIdentifiers.Count > 0)
            dependencies.Add(MsSqlComponentId);
        if (UsedServiceBusIdentifiers.Count > 0)
            dependencies.Add(ServiceBusComponentId);

        return dependencies;
    }

    /// <summary>
    /// Chooses what happens to emulator contents a previous run left behind.
    /// </summary>
    /// <param name="mode">The reset mode to use.</param>
    /// <returns>The same environment, so calls can be chained.</returns>
    public DockerAzureEnvironment UseResetMode(AzureResetMode mode)
    {
        _resetMode = mode;
        return this;
    }

    internal AzureResetMode GetResetMode() => _resetMode;

    /// <summary>
    /// Whether this run was handed emulator containers that were already running.
    /// </summary>
    /// <remarks>
    /// A run that started its own containers has nothing to purge, so the reset can be skipped whole.
    /// The persistent context seeds the reused state before any component of the run executes, which is
    /// what makes this readable at the point the reset needs it.
    /// </remarks>
    internal bool HasReusedPersistentComponents
    {
        get
        {
            lock (_reusedGate)
                return _reusedPersistentComponents.Count > 0;
        }
    }

    internal IReadOnlyCollection<ComponentContractBinding> GetContractBindings()
    {
        return _definitionState.LastContractBindings;
    }

    internal ServiceBusTopologySource GetServiceBusTopologySource()
    {
        // Resolution refuses a run that uses Service Bus without a topology, so reaching this without one means a
        // component was created without resolving first.
        return _definitionState.ServiceBusTopologySource
            ?? throw new FrameworkStateException("The Service Bus component was created, and no topology was resolved for it.");
    }

    internal string GetServiceBusTopologyConfigPath()
    {
        ServiceBusTopologySource source = GetServiceBusTopologySource();
        return source.ConfigPath ?? throw new FrameworkStateException("The active Service Bus topology was configured fluently and does not have a backing file path.");
    }

    internal string GetAzuriteImage()
    {
        return _definitionState.AzuriteImage ?? DockerAzureDefaults.AzuriteImage;
    }

    internal string GetCosmosDbImage()
    {
        return _definitionState.CosmosDbImage ?? DockerAzureDefaults.CosmosDbImage;
    }

    internal string GetMsSqlImage()
    {
        return _definitionState.MsSqlImage ?? DockerAzureDefaults.MsSqlImage;
    }

    internal int GetMsSqlMemoryLimitMb()
    {
        return _definitionState.MsSqlMemoryLimitMb ?? DockerAzureDefaults.MsSqlMemoryLimitMb;
    }

    internal TimeSpan GetMsSqlReadinessTimeout()
    {
        return _definitionState.MsSqlReadinessTimeout ?? DockerAzureDefaults.MsSqlReadinessTimeout;
    }

    internal string GetServiceBusImage()
    {
        return _definitionState.ServiceBusImage ?? DockerAzureDefaults.ServiceBusImage;
    }

    /// <summary>
    /// The <c>sa</c> password a <see cref="DockerAzureInfrastructureDefinition.MsSqlPassword"/> pinned, or
    /// <see langword="null"/> when each SQL Server container generates its own.
    /// </summary>
    internal string? PinnedMsSqlPassword => _definitionState.MsSqlPassword;

    /// <summary>
    /// Wraps <paramref name="baseServiceProvider"/> so that it also resolves this environment as
    /// <see cref="DockerAzureEnvironment"/> for the run.
    /// </summary>
    /// <param name="baseServiceProvider">The service provider configured for the run.</param>
    /// <returns>The run's service provider.</returns>
    public IServiceProvider CreateRunScopedServiceProvider(IServiceProvider baseServiceProvider)
    {
        return new DockerAzureRunScopedServiceProvider(this, baseServiceProvider);
    }

    /// <summary>
    /// Creates the instance one run uses: this environment's definitions and reset mode, and nothing any run decided.
    /// </summary>
    /// <remarks>
    /// The instance records what its run resolved - the identifiers in use, the resolution summary, which emulators
    /// an earlier run left running - and two runs sharing one instance would read and overwrite each other's. Sealed
    /// for the same reason: a copy made here would quietly drop whatever a subclass added.
    /// </remarks>
    /// <returns>A fresh environment for one run.</returns>
    public IEnvironmentProvider CreateForRun() => CloneDefinitions();

    internal DockerAzureEnvironment CloneDefinitions()
    {
        DockerAzureEnvironment clone = new();

        clone._resetMode = _resetMode;

        foreach (DockerAzureDefinition definition in _includedDefinitions.Values)
            clone.Include(definition);

        return clone;
    }
}