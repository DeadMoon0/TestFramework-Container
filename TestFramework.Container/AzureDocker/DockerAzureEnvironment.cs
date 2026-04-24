using System.Reflection;
using TestFramework.Azure;
using TestFramework.Azure.DB.CosmosDB;
using TestFramework.Azure.DB.SqlServer;
using TestFramework.Azure.Identifier;
using TestFramework.Azure.FunctionApp;
using TestFramework.Azure.StorageAccount.Blob;
using TestFramework.Azure.StorageAccount.Table;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;

namespace TestFramework.Container.AzureDocker;

public class DockerAzureEnvironment : EnvironmentProviderBase
{
    public static readonly EnvComponentIdentifier NetworkComponentId = "docker-network";
    public static readonly EnvComponentIdentifier FunctionAppComponentId = "functionapp";
    public static readonly EnvComponentIdentifier MsSqlComponentId = "mssql";
    public static readonly EnvComponentIdentifier AzuriteComponentId = "azurite";
    public static readonly EnvComponentIdentifier CosmosDbComponentId = "cosmos-emulator";
    public static readonly EnvComponentIdentifier ServiceBusComponentId = "servicebus-emulator";
    public const string AzuriteNetworkAlias = "azurite";
    public const string CosmosDbNetworkAlias = "cosmos-emulator";
    public const string ServiceBusNetworkAlias = "servicebus-emulator";

    private readonly Dictionary<EnvComponentIdentifier, object?> _runtimeStates = [];

    public DockerAzureEnvironmentOptions Options { get; }
    public HashSet<string> UsedStorageIdentifiers { get; } = [];
    public HashSet<string> UsedCosmosIdentifiers { get; } = [];
    public HashSet<string> UsedSqlIdentifiers { get; } = [];
    public HashSet<string> UsedServiceBusIdentifiers { get; } = [];
    public HashSet<string> UsedFunctionAppIdentifiers { get; } = [];
    internal Dictionary<string, string> CosmosPartitionKeyPaths { get; } = [];

    public DockerAzureEnvironment(DockerAzureEnvironmentOptions? options = null)
    {
        Options = options ?? new DockerAzureEnvironmentOptions();

        AddComponent(new Components.DockerNetworkEnvComponent());
        AddComponent(new Components.FunctionAppEnvComponent());
        AddComponent(new Components.MsSqlEnvComponent());
        AddComponent(new Components.AzuriteEnvComponent());
        AddComponent(new Components.CosmosDbEnvComponent());
        AddComponent(new Components.ServiceBusEnvComponent());

        MapResourceKind(AzureEnvironmentResourceKinds.FunctionApp, FunctionAppComponentId);
        MapResourceKind(AzureEnvironmentResourceKinds.Storage, AzuriteComponentId);
        MapResourceKind(AzureEnvironmentResourceKinds.Cosmos, CosmosDbComponentId);
        MapResourceKind(AzureEnvironmentResourceKinds.Sql, MsSqlComponentId);
        MapResourceKind(AzureEnvironmentResourceKinds.ServiceBus, ServiceBusComponentId);

        MapArtifact<StorageAccountBlobArtifactDescriber>(AzuriteComponentId);
        MapArtifact(typeof(TableStorageEntityArtifactDescriber<>), AzuriteComponentId);
        MapArtifact(typeof(CosmosDbItemArtifactDescriber<>), CosmosDbComponentId);
        MapArtifact(typeof(SqlRowArtifactDescriber<>), MsSqlComponentId);
    }

    public override IReadOnlyCollection<EnvComponentIdentifier> ResolveComponents(IEnumerable<ArtifactInstanceGeneric> artifacts, IEnumerable<EnvironmentRequirement> requirements)
    {
        UsedStorageIdentifiers.Clear();
        UsedCosmosIdentifiers.Clear();
        UsedSqlIdentifiers.Clear();
        UsedServiceBusIdentifiers.Clear();
        UsedFunctionAppIdentifiers.Clear();
        CosmosPartitionKeyPaths.Clear();

        foreach (FunctionAppIdentifier identifier in Options.RequiredFunctionAppIdentifiers)
            UsedFunctionAppIdentifiers.Add(identifier);
        foreach (StorageAccountIdentifier identifier in Options.RequiredStorageIdentifiers)
            UsedStorageIdentifiers.Add(identifier);
        foreach (CosmosContainerIdentifier identifier in Options.RequiredCosmosIdentifiers)
            UsedCosmosIdentifiers.Add(identifier);
        foreach (SqlDatabaseIdentifier identifier in Options.RequiredSqlIdentifiers)
            UsedSqlIdentifiers.Add(identifier);
        foreach (ServiceBusIdentifier identifier in Options.RequiredServiceBusIdentifiers)
            UsedServiceBusIdentifiers.Add(identifier);

        foreach (ArtifactInstanceGeneric artifact in artifacts)
            CaptureIdentifiers(artifact.Reference);

        HashSet<EnvComponentIdentifier> resolved = [.. base.ResolveComponents(artifacts, requirements), .. Options.RequiredComponents];
        if (UsedServiceBusIdentifiers.Count > 0)
            resolved.Add(ServiceBusComponentId);

        return [.. resolved];
    }

    internal void SetRuntimeState(EnvComponentIdentifier identifier, object? state)
    {
        _runtimeStates[identifier] = state;
    }

    internal T GetRequiredRuntimeState<T>(EnvComponentIdentifier identifier)
    {
        if (_runtimeStates.TryGetValue(identifier, out object? state) && state is T typedState)
            return typedState;

        throw new InvalidOperationException($"The runtime state for environment component '{identifier}' is not available.");
    }

    private void CaptureIdentifiers(ArtifactReferenceGeneric reference)
    {
        Type referenceType = reference.GetType();
        if (reference is StorageAccountBlobArtifactReference blobReference)
        {
            UsedStorageIdentifiers.Add(blobReference.Identifier);
            return;
        }

        if (TryReadIdentifier(reference, referenceType, "Identifier", out string? storageIdentifier))
            UsedStorageIdentifiers.Add(storageIdentifier);

        if (TryReadIdentifier(reference, referenceType, "DbIdentifier", out string? databaseIdentifier))
        {
            if (MatchesGenericType(referenceType, typeof(CosmosDbItemArtifactReference<>)))
            {
                UsedCosmosIdentifiers.Add(databaseIdentifier);
                RegisterCosmosSchema(databaseIdentifier, referenceType.GetGenericArguments()[0]);
            }
            else if (MatchesGenericType(referenceType, typeof(SqlRowArtifactReference<>)))
                UsedSqlIdentifiers.Add(databaseIdentifier);
        }
    }

    private void RegisterCosmosSchema(string identifier, Type modelType)
    {
        string partitionKeyPath = CosmosModelSchemaResolver.ResolvePartitionKeyPath(modelType);
        if (CosmosPartitionKeyPaths.TryGetValue(identifier, out string? existingPath) && !string.Equals(existingPath, partitionKeyPath, StringComparison.Ordinal))
            throw new InvalidOperationException($"Cosmos identifier '{identifier}' was configured with conflicting partition key paths: '{existingPath}' and '{partitionKeyPath}'.");

        CosmosPartitionKeyPaths[identifier] = partitionKeyPath;
    }

    private static bool TryReadIdentifier(object instance, Type instanceType, string propertyName, out string value)
    {
        PropertyInfo? property = instanceType.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property?.GetValue(instance) is not null)
        {
            value = property.GetValue(instance)!.ToString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool MatchesGenericType(Type candidate, Type genericTypeDefinition)
    {
        return candidate.IsGenericType && candidate.GetGenericTypeDefinition() == genericTypeDefinition;
    }

    protected override void OnRequirementResolved(EnvironmentRequirement requirement)
    {
        switch (requirement.ResourceKind)
        {
            case AzureEnvironmentResourceKinds.Storage:
                UsedStorageIdentifiers.Add(requirement.ResourceIdentifier);
                break;
            case AzureEnvironmentResourceKinds.Cosmos:
                UsedCosmosIdentifiers.Add(requirement.ResourceIdentifier);
                break;
            case AzureEnvironmentResourceKinds.Sql:
                UsedSqlIdentifiers.Add(requirement.ResourceIdentifier);
                break;
            case AzureEnvironmentResourceKinds.ServiceBus:
                UsedServiceBusIdentifiers.Add(requirement.ResourceIdentifier);
                break;
            case AzureEnvironmentResourceKinds.FunctionApp:
                UsedFunctionAppIdentifiers.Add(requirement.ResourceIdentifier);
                break;
        }
    }
}