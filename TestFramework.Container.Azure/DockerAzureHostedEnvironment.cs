using System.Reflection;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Debugger;
using TestFramework.Core.Environment;
using TestFramework.Core.Logging;
using TestFramework.Core.Steps;
using TestFramework.Core.Variables;

namespace TestFramework.Container.Azure;

/// <summary>
/// Boots a configured Docker Azure environment once and reuses the hosted runtime state across multiple timeline runs.
/// </summary>
public sealed class DockerAzureHostedEnvironment : IAsyncDisposable
{
    private readonly DockerAzureEnvironment _bootstrapEnvironment;
    private readonly IReadOnlyCollection<EnvironmentRequirement> _bootstrapRequirements;
    private readonly Dictionary<EnvComponentIdentifier, object?> _sharedStates = [];
    private readonly List<EnvComponentIdentifier> _creationOrder = [];
    private readonly IServiceProvider _bootstrapServiceProvider;
    private readonly bool _disposeBootstrapServiceProvider;

    private DockerAzureHostedEnvironment(
        DockerAzureEnvironment bootstrapEnvironment,
        IServiceProvider bootstrapServiceProvider,
        IReadOnlyCollection<EnvironmentRequirement> bootstrapRequirements,
        bool disposeBootstrapServiceProvider)
    {
        _bootstrapEnvironment = bootstrapEnvironment;
        _bootstrapServiceProvider = bootstrapServiceProvider;
        _bootstrapRequirements = bootstrapRequirements;
        _disposeBootstrapServiceProvider = disposeBootstrapServiceProvider;
    }

    /// <summary>
    /// Starts a hosted Docker Azure environment that can be reused across multiple runs.
    /// </summary>
    public static async Task<DockerAzureHostedEnvironment> StartAsync(
        DockerAzureEnvironment environment,
        IServiceProvider bootstrapServiceProvider,
        IReadOnlyCollection<EnvironmentRequirement> bootstrapRequirements,
        bool disposeBootstrapServiceProvider = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(bootstrapServiceProvider);
        ArgumentNullException.ThrowIfNull(bootstrapRequirements);

        DockerAzureHostedEnvironment hostedEnvironment = new(
            environment.CloneDefinitions(),
            bootstrapServiceProvider,
            bootstrapRequirements,
            disposeBootstrapServiceProvider);

        await hostedEnvironment.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return hostedEnvironment;
    }

    /// <summary>
    /// Creates a fresh environment provider for a single timeline run while reusing the hosted Docker runtime state.
    /// </summary>
    public IEnvironmentProvider CreateEnvironment()
    {
        return new HostedRunDockerAzureEnvironment(
            _bootstrapEnvironment.CloneDefinitions(),
            _sharedStates,
            _bootstrapServiceProvider);
    }

    /// <summary>
    /// Disposes the shared Docker Azure resources created for the hosted environment.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_creationOrder.Count > 0)
        {
            ScopedLogger logger = BootstrapReflectionContext.CreateLogger();
            ArtifactStore artifactStore = BootstrapReflectionContext.CreateArtifactStore(logger);
            VariableStore variableStore = BootstrapReflectionContext.CreateVariableStore(logger);

            foreach (EnvComponentIdentifier componentId in Enumerable.Reverse(_creationOrder))
            {
                EnvComponent component = _bootstrapEnvironment.GetComponent(componentId);
                _sharedStates.TryGetValue(componentId, out object? state);
                await component.DeconstructAsync(state, _bootstrapEnvironment, _bootstrapServiceProvider, variableStore, artifactStore, logger, CancellationToken.None).ConfigureAwait(false);
            }
        }

        if (_disposeBootstrapServiceProvider)
        {
            switch (_bootstrapServiceProvider)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        IReadOnlyCollection<EnvComponentIdentifier> resolvedComponents = _bootstrapEnvironment.ResolveComponents([], _bootstrapRequirements);
        ScopedLogger logger = BootstrapReflectionContext.CreateLogger();
        ArtifactStore artifactStore = BootstrapReflectionContext.CreateArtifactStore(logger);
        VariableStore variableStore = BootstrapReflectionContext.CreateVariableStore(logger);

        foreach (EnvComponentIdentifier componentId in OrderComponents(_bootstrapEnvironment, resolvedComponents))
        {
            EnvComponent component = _bootstrapEnvironment.GetComponent(componentId);
            object? state = await component.CreateAsync(_bootstrapEnvironment, _bootstrapServiceProvider, variableStore, artifactStore, logger, cancellationToken).ConfigureAwait(false);
            _sharedStates[componentId] = state;
            _creationOrder.Add(componentId);
        }
    }

    private static IReadOnlyList<EnvComponentIdentifier> OrderComponents(IEnvironmentProvider environment, IEnumerable<EnvComponentIdentifier> rootComponents)
    {
        List<EnvComponentIdentifier> ordered = [];
        HashSet<EnvComponentIdentifier> visiting = [];
        HashSet<EnvComponentIdentifier> visited = [];

        foreach (EnvComponentIdentifier rootComponent in rootComponents)
            Visit(environment, rootComponent, visiting, visited, ordered);

        return ordered;
    }

    private static void Visit(
        IEnvironmentProvider environment,
        EnvComponentIdentifier identifier,
        HashSet<EnvComponentIdentifier> visiting,
        HashSet<EnvComponentIdentifier> visited,
        List<EnvComponentIdentifier> ordered)
    {
        if (visited.Contains(identifier))
            return;

        if (!visiting.Add(identifier))
            throw new InvalidOperationException($"A cyclic environment component dependency was detected at '{identifier}'.");

        EnvComponent component = environment.GetComponent(identifier);
        foreach (EnvComponentIdentifier dependency in component.Dependencies)
            Visit(environment, dependency, visiting, visited, ordered);

        visiting.Remove(identifier);
        visited.Add(identifier);
        ordered.Add(identifier);
    }

    private sealed class HostedRunDockerAzureEnvironment : IEnvironmentProvider, IRunScopedServiceProviderFactory
    {
        private readonly DockerAzureEnvironment _environment;
        private readonly IReadOnlyDictionary<EnvComponentIdentifier, object?> _sharedStates;
        private readonly IServiceProvider _bootstrapServiceProvider;
        private readonly Dictionary<EnvComponentIdentifier, EnvComponent> _components = [];

        public HostedRunDockerAzureEnvironment(
            DockerAzureEnvironment environment,
            IReadOnlyDictionary<EnvComponentIdentifier, object?> sharedStates,
            IServiceProvider bootstrapServiceProvider)
        {
            _environment = environment;
            _sharedStates = sharedStates;
            _bootstrapServiceProvider = bootstrapServiceProvider;
        }

        public bool SupportsParallelComponentCreation => _environment.SupportsParallelComponentCreation;

        public IReadOnlyCollection<EnvComponentIdentifier> ResolveComponents(IEnumerable<ArtifactInstanceGeneric> artifacts, IEnumerable<EnvironmentRequirement> requirements)
            => _environment.ResolveComponents(artifacts, requirements);

        public EnvComponent GetComponent(EnvComponentIdentifier identifier)
        {
            if (_components.TryGetValue(identifier, out EnvComponent? component))
                return component;

            EnvComponent realComponent = _environment.GetComponent(identifier);
            component = new HostedStateEnvComponent(identifier, realComponent.Dependencies, _sharedStates);
            _components[identifier] = component;
            return component;
        }

        public IServiceProvider CreateRunScopedServiceProvider(IServiceProvider baseServiceProvider)
            => new DockerAzureRunScopedServiceProvider(_environment, baseServiceProvider, _bootstrapServiceProvider);
    }

    private sealed class HostedStateEnvComponent(
        EnvComponentIdentifier id,
        IReadOnlyList<EnvComponentIdentifier> dependencies,
        IReadOnlyDictionary<EnvComponentIdentifier, object?> sharedStates) : EnvComponent
    {
        public override EnvComponentIdentifier Id => id;

        public override IReadOnlyList<EnvComponentIdentifier> Dependencies => dependencies;

        public override Task<object?> CreateAsync(IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
        {
            sharedStates.TryGetValue(Id, out object? state);
            return Task.FromResult(state);
        }

        public override Task DeconstructAsync(object? state, IEnvironmentProvider environment, IServiceProvider serviceProvider, VariableStore variableStore, ArtifactStore artifactStore, ScopedLogger logger, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private static class BootstrapReflectionContext
    {
        private static readonly Type DebuggingRunSessionType = typeof(TimelineRunStructure).Assembly.GetType("TestFramework.Core.Debugger.DebuggingRunSession")
            ?? throw new InvalidOperationException("Could not locate DebuggingRunSession.");
        private static readonly ConstructorInfo ScopedLoggerConstructor = typeof(ScopedLogger)
            .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, binder: null, [typeof(Xunit.Abstractions.ITestOutputHelper)], modifiers: null)
            ?? throw new InvalidOperationException("Could not locate the internal ScopedLogger constructor.");
        private static readonly ConstructorInfo ArtifactStoreConstructor = typeof(ArtifactStore)
            .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, binder: null, [typeof(ScopedLogger), DebuggingRunSessionType], modifiers: null)
            ?? throw new InvalidOperationException("Could not locate the internal ArtifactStore constructor.");
        private static readonly ConstructorInfo VariableStoreConstructor = typeof(VariableStore)
            .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, binder: null, [typeof(ScopedLogger), DebuggingRunSessionType], modifiers: null)
            ?? throw new InvalidOperationException("Could not locate the internal VariableStore constructor.");
        private static readonly ConstructorInfo DebuggingRunSessionConstructor = DebuggingRunSessionType
            .GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null, [typeof(IRunDebugger)], modifiers: null)
            ?? throw new InvalidOperationException("Could not locate the DebuggingRunSession constructor.");

        public static ScopedLogger CreateLogger() => (ScopedLogger)ScopedLoggerConstructor.Invoke([null]);

        public static ArtifactStore CreateArtifactStore(ScopedLogger logger)
            => (ArtifactStore)ArtifactStoreConstructor.Invoke([logger, CreateDebuggingRunSession()]);

        public static VariableStore CreateVariableStore(ScopedLogger logger)
            => (VariableStore)VariableStoreConstructor.Invoke([logger, CreateDebuggingRunSession()]);

        private static object CreateDebuggingRunSession()
            => DebuggingRunSessionConstructor.Invoke([new NullRunDebugger()]);

        private sealed class NullRunDebugger : IRunDebugger
        {
            public static IRunDebugger CreateNew() => new NullRunDebugger();

            public Task SignalInitTimelineRunAsync(string sessionId, string name, string projectPath, TimelineRunStructure runStructure) => Task.CompletedTask;

            public Task SignalStageBeginAsync(string sessionId, string name) => Task.CompletedTask;

            public Task SignalStepBeginAsync(string sessionId, int stepId) => Task.CompletedTask;

            public Task SignalStepResultChangeAsync(string sessionId, StepResultGeneric result) => Task.CompletedTask;

            public Task SignalVariableUpdateAsync(string sessionId, string name, VariableState variable) => Task.CompletedTask;

            public Task SignalArtifactUpdateAsync(string sessionId, string name, TestFramework.Core.Debugger.ArtifactState artifact) => Task.CompletedTask;

            public Task SignalTimelineRunFinishedAsync(string sessionId) => Task.CompletedTask;

            public Task SignalAndWaitBreakpointHitAsync(string sessionId, string stage, int stepId) => Task.CompletedTask;
        }
    }
}