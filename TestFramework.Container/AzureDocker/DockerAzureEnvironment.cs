using System.Reflection;
using TestFramework.Azure;
using TestFramework.Azure.DB.CosmosDB;
using TestFramework.Azure.DB.SqlServer;
using TestFramework.Azure.Identifier;
using TestFramework.Azure.StorageAccount.Blob;
using TestFramework.Azure.StorageAccount.Table;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;

namespace TestFramework.Container.AzureDocker;

public class DockerAzureEnvironment : EnvironmentProviderBase
{
    public static readonly EnvComponentIdentifier NetworkComponentId = "docker-network";
    public static readonly EnvComponentIdentifier MsSqlComponentId = "mssql";
    public static readonly EnvComponentIdentifier AzuriteComponentId = "azurite";
    public static readonly EnvComponentIdentifier CosmosDbComponentId = "cosmos-emulator";
    public static readonly EnvComponentIdentifier ServiceBusComponentId = "servicebus-emulator";

    private readonly Dictionary<EnvComponentIdentifier, object?> _runtimeStates = [];

    public DockerAzureEnvironmentOptions Options { get; }
    public HashSet<string> UsedStorageIdentifiers { get; } = [];
    public HashSet<string> UsedCosmosIdentifiers { get; } = [];
    public HashSet<string> UsedSqlIdentifiers { get; } = [];
    public HashSet<string> UsedServiceBusIdentifiers { get; } = [];

    public DockerAzureEnvironment(DockerAzureEnvironmentOptions? options = null)
    {
        Options = options ?? new DockerAzureEnvironmentOptions();

        AddComponent(new Components.DockerNetworkEnvComponent());
        AddComponent(new Components.MsSqlEnvComponent());
        AddComponent(new Components.AzuriteEnvComponent());
        AddComponent(new Components.CosmosDbEnvComponent());
        AddComponent(new Components.ServiceBusEnvComponent());

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
                UsedCosmosIdentifiers.Add(databaseIdentifier);
            else if (MatchesGenericType(referenceType, typeof(SqlRowArtifactReference<>)))
                UsedSqlIdentifiers.Add(databaseIdentifier);
        }
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
        }
    }
}