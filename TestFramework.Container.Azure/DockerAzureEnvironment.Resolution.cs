using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TestFramework.Azure;
using TestFramework.Azure.DB.CosmosDb;
using TestFramework.Azure.DB.SqlDatabase;
using TestFramework.Azure.Identifier;
using TestFramework.Azure.StorageAccount.Blob;
using TestFramework.Azure.StorageAccount.Table;
using TestFramework.Core.Artifacts;
using TestFramework.Core.Environment;
using TestFramework.Core.Exceptions;
using TestFramework.Core.Logging;

namespace TestFramework.Container.Azure;

public sealed partial class DockerAzureEnvironment
{
    /// <summary>
    /// Works out which containers the run needs: the resources its requirements and artifacts name, the definitions
    /// those depend on or are contract-bound to, and the per-run reset when an emulator is in use.
    /// </summary>
    /// <remarks>
    /// Clears and refills the used-identifier collections, such as <see cref="UsedStorageAccountIdentifiers"/>.
    /// </remarks>
    /// <param name="artifacts">Artifacts already available for the run.</param>
    /// <param name="requirements">The environment requirements routed to this environment.</param>
    /// <returns>The identifiers of the components the run needs.</returns>
    /// <exception cref="FrameworkConfigurationException">
    /// A used Function App has no registration, or Service Bus is used without a topology.
    /// </exception>
    public override IReadOnlyCollection<EnvComponentIdentifier> ResolveComponents(IEnumerable<ArtifactInstanceGeneric> artifacts, IEnumerable<EnvironmentRequirement> requirements)
    {
        _usedStorageAccountIdentifiers.Clear();
        _usedCosmosDbIdentifiers.Clear();
        _usedSqlDatabaseIdentifiers.Clear();
        _usedServiceBusIdentifiers.Clear();
        _usedFunctionAppIdentifiers.Clear();
        CosmosPartitionKeyPaths.Clear();
        _resolutionSummaryLogged = false;

        // What the run needs arrives as this call's input - each requirement the engine routed here,
        // already checked against the run's resources - rather than through the obsolete per-requirement
        // hook. A Logic App requirement never arrives: this environment does not stand in for that kind, so
        // a run can drive a live Logic App beside containerised storage.
        foreach (EnvironmentRequirement requirement in requirements)
            CaptureRequirement(requirement);

        foreach (ArtifactInstanceGeneric artifact in artifacts)
            CaptureIdentifiers(artifact.Reference);

        IReadOnlyCollection<ComponentContractBinding> contractBindings = _definitionState.ValidateAndBindContracts(DockerAzureContractMatcher.IsMatch);
        HashSet<EnvComponentIdentifier> resolved = [.. base.ResolveComponents(artifacts, requirements)];
        CaptureActivatedDefinitionUsage(_definitionState.ExpandActivatedDefinitions(
            UsedFunctionAppIdentifiers.Select(x => new FunctionAppIdentifier(x)),
            UsedStorageAccountIdentifiers.Select(x => new StorageAccountIdentifier(x)),
            UsedCosmosDbIdentifiers.Select(x => new CosmosDbIdentifier(x)),
            UsedSqlDatabaseIdentifiers.Select(x => new SqlDatabaseIdentifier(x)),
            UsedServiceBusIdentifiers.Select(x => new ServiceBusIdentifier(x)),
            contractBindings));

        if (UsedFunctionAppIdentifiers.Count > 0)
            resolved.Add(FunctionAppComponentId);
        if (UsedStorageAccountIdentifiers.Count > 0)
            resolved.Add(AzuriteComponentId);
        if (UsedCosmosDbIdentifiers.Count > 0)
            resolved.Add(CosmosDbComponentId);
        if (UsedSqlDatabaseIdentifiers.Count > 0)
            resolved.Add(MsSqlComponentId);
        if (UsedServiceBusIdentifiers.Count > 0)
            resolved.Add(ServiceBusComponentId);

        // Whatever emulator resolved may be carrying a previous run's data.
        if (UsedStorageAccountIdentifiers.Count > 0 || UsedCosmosDbIdentifiers.Count > 0 || UsedSqlDatabaseIdentifiers.Count > 0 || UsedServiceBusIdentifiers.Count > 0)
            resolved.Add(AzureResetComponentId);

        ValidateFunctionAppRegistrations();
        EnsureServiceBusTopology();
        _lastResolutionSnapshot = CreateResolutionSnapshot(resolved, contractBindings);

        return [.. resolved];
    }

    /// <summary>
    /// Refuses a run that uses Service Bus when no definition says which queues and topics the emulator creates.
    /// </summary>
    /// <remarks>
    /// Here, while the run is planned, rather than when the emulator starts: by then SQL Server is already up, and the
    /// failure used to be a file-not-found for a default path that only ever existed in this package's own output.
    /// </remarks>
    private void EnsureServiceBusTopology()
    {
        if (UsedServiceBusIdentifiers.Count == 0 || _definitionState.ServiceBusTopologySource is not null)
            return;

        throw new FrameworkConfigurationException(
            $"The run uses Service Bus ({string.Join(", ", UsedServiceBusIdentifiers.Order(StringComparer.Ordinal).Select(identifier => $"'{identifier}'"))}), and no definition says which queues and topics the emulator should create.")
        {
            RecoverySteps =
            [
                "Override ConfigureServiceBusTopology on the DockerServiceBusDefinition for it, or on a DockerAzureInfrastructureDefinition.",
                "Or set TopologyConfigPath to an emulator config file your test project copies to its output.",
            ],
        };
    }

    internal void LogPendingResolutionSummary(ScopedLogger logger)
    {
        if (_resolutionSummaryLogged || _lastResolutionSnapshot == DockerAzureResolutionSnapshot.Empty)
            return;

        _resolutionSummaryLogged = true;
        foreach (string line in _lastResolutionSnapshot.ToLogLines())
            logger.LogInformation(line);
    }

    /// <summary>
    /// Records which resource an artifact this run holds lives in, for the four artifact kinds this environment serves.
    /// </summary>
    /// <remarks>
    /// Matched on the concrete reference types. It used to match on a property <em>name</em>: any reference with a
    /// public <c>Identifier</c> property counted as a storage account and started Azurite, whatever package it came
    /// from. The property names below are written through <c>nameof</c>, so a rename in the Azure package breaks
    /// this build instead of quietly matching nothing.
    /// </remarks>
    private void CaptureIdentifiers(ArtifactReferenceGeneric reference)
    {
        if (reference is StorageAccountBlobArtifactReference blob)
        {
            _usedStorageAccountIdentifiers.Add(blob.Identifier);
            return;
        }

        if (ClosedOver(reference.GetType(), typeof(StorageAccountTableEntityArtifactReference<>)) is { } table)
        {
            _usedStorageAccountIdentifiers.Add(Read(reference, table, nameof(StorageAccountTableEntityArtifactReference<global::Azure.Data.Tables.TableEntity>.Identifier)));
        }
        else if (ClosedOver(reference.GetType(), typeof(CosmosDbItemArtifactReference<>)) is { } cosmos)
        {
            string identifier = Read(reference, cosmos, nameof(CosmosDbItemArtifactReference<object>.DbIdentifier));
            _usedCosmosDbIdentifiers.Add(identifier);
            RegisterCosmosSchema(identifier, cosmos.GetGenericArguments()[0]);
        }
        else if (ClosedOver(reference.GetType(), typeof(SqlDatabaseRowArtifactReference<>)) is { } sql)
        {
            _usedSqlDatabaseIdentifiers.Add(Read(reference, sql, nameof(SqlDatabaseRowArtifactReference<object>.DbIdentifier)));
        }
    }

    /// <summary>
    /// The closed form of a generic reference type this reference is, or derives from.
    /// </summary>
    private static Type? ClosedOver(Type type, Type genericTypeDefinition)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == genericTypeDefinition)
                return current;
        }

        return null;
    }

    private static string Read(object reference, Type declaringType, string propertyName)
        => declaringType.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)!.GetValue(reference)!.ToString()!;

    private DockerAzureResolutionSnapshot CreateResolutionSnapshot(IEnumerable<EnvComponentIdentifier> resolvedComponents, IReadOnlyCollection<ComponentContractBinding> contractBindings)
    {
        return new DockerAzureResolutionSnapshot(
            [.. resolvedComponents.Select(component => component.ToString()).OrderBy(x => x, StringComparer.Ordinal)],
            [.. UsedFunctionAppIdentifiers.OrderBy(x => x, StringComparer.Ordinal)],
            [.. UsedStorageAccountIdentifiers.OrderBy(x => x, StringComparer.Ordinal)],
            [.. UsedCosmosDbIdentifiers.OrderBy(x => x, StringComparer.Ordinal)],
            [.. UsedSqlDatabaseIdentifiers.OrderBy(x => x, StringComparer.Ordinal)],
            [.. UsedServiceBusIdentifiers.OrderBy(x => x, StringComparer.Ordinal)],
            [.. contractBindings.Select(FormatContractBinding).OrderBy(x => x, StringComparer.Ordinal)]);
    }

    private static string FormatContractBinding(ComponentContractBinding binding)
    {
        return $"{binding.ConsumerIdentity} <= {binding.ProviderIdentity} ({binding.Requirement.GetType().Name})";
    }

    private void ValidateFunctionAppRegistrations()
    {
        if (UsedFunctionAppIdentifiers.Count == 0)
            return;

        HashSet<string> configuredIdentifiers = [.. GetFunctionAppRegistrations().Select(x => x.Identifier)];
        string[] missingIdentifiers = [.. UsedFunctionAppIdentifiers.Where(identifier => !configuredIdentifiers.Contains(identifier)).OrderBy(identifier => identifier, StringComparer.Ordinal)];
        if (missingIdentifiers.Length > 0)
            throw new FrameworkConfigurationException($"No Docker Function App registration was configured for: {string.Join(", ", missingIdentifiers)}.");
    }

    private void CaptureActivatedDefinitionUsage(IEnumerable<DockerAzureDefinitionMetadata> definitions)
    {
        foreach (DockerAzureDefinitionMetadata metadata in definitions)
        {
            switch (metadata.Definition)
            {
                case DockerStorageAccountDefinition storage:
                    _usedStorageAccountIdentifiers.Add(storage.Identifier);
                    break;
                case DockerCosmosDbDefinition cosmos:
                    _usedCosmosDbIdentifiers.Add(cosmos.Identifier);
                    if (cosmos.ModelType is not null)
                        RegisterCosmosSchema(cosmos.Identifier, cosmos.ModelType);
                    break;
                case DockerSqlDatabaseDefinition sql:
                    _usedSqlDatabaseIdentifiers.Add(sql.Identifier);
                    break;
                case DockerServiceBusDefinition serviceBus:
                    _usedServiceBusIdentifiers.Add(serviceBus.Identifier);
                    break;
                case DockerFunctionAppDefinition functionApp:
                    _usedFunctionAppIdentifiers.Add(functionApp.Identifier);
                    break;
            }
        }
    }

    private void RegisterCosmosSchema(string identifier, Type modelType)
    {
        string partitionKeyPath = CosmosModelSchemaResolver.ResolvePartitionKeyPath(modelType);
        if (CosmosPartitionKeyPaths.TryGetValue(identifier, out string? existingPath) && !string.Equals(existingPath, partitionKeyPath, StringComparison.Ordinal))
            throw new FrameworkConfigurationException($"Cosmos identifier '{identifier}' was configured with conflicting partition key paths: '{existingPath}' and '{partitionKeyPath}'.");

        CosmosPartitionKeyPaths[identifier] = partitionKeyPath;
    }

    private void CaptureRequirement(EnvironmentRequirement requirement)
    {
        switch (requirement.ResourceKind)
        {
            case AzureResourceKinds.StorageAccount:
                _usedStorageAccountIdentifiers.Add(requirement.ResourceIdentifier);
                break;
            case AzureResourceKinds.CosmosDb:
                _usedCosmosDbIdentifiers.Add(requirement.ResourceIdentifier);
                break;
            case AzureResourceKinds.SqlDatabase:
                _usedSqlDatabaseIdentifiers.Add(requirement.ResourceIdentifier);
                break;
            case AzureResourceKinds.ServiceBus:
                _usedServiceBusIdentifiers.Add(requirement.ResourceIdentifier);
                break;
            case AzureResourceKinds.FunctionApp:
                _usedFunctionAppIdentifiers.Add(requirement.ResourceIdentifier);
                break;
        }
    }

    internal sealed record DockerAzureResolutionSnapshot(
        IReadOnlyCollection<string> ResolvedComponents,
        IReadOnlyCollection<string> FunctionApps,
        IReadOnlyCollection<string> Storage,
        IReadOnlyCollection<string> Cosmos,
        IReadOnlyCollection<string> Sql,
        IReadOnlyCollection<string> ServiceBus,
        IReadOnlyCollection<string> ContractBindings)
    {
        internal static DockerAzureResolutionSnapshot Empty { get; } = new([], [], [], [], [], [], []);

        internal IReadOnlyCollection<string> ToLogLines()
        {
            List<string> lines =
            [
                $"Docker Azure resolution: components [{JoinOrNone(ResolvedComponents)}]"
            ];

            AddIfAny(lines, "Function Apps", FunctionApps);
            AddIfAny(lines, "Storage", Storage);
            AddIfAny(lines, "Cosmos", Cosmos);
            AddIfAny(lines, "SQL", Sql);
            AddIfAny(lines, "Service Bus", ServiceBus);

            if (ContractBindings.Count > 0)
                lines.Add($"Docker Azure contracts: {JoinOrNone(ContractBindings)}");

            return lines;
        }

        private static void AddIfAny(List<string> lines, string label, IReadOnlyCollection<string> values)
        {
            if (values.Count > 0)
                lines.Add($"Docker Azure {label}: {JoinOrNone(values)}");
        }

        private static string JoinOrNone(IReadOnlyCollection<string> values)
            => values.Count == 0 ? "none" : string.Join(", ", values);
    }
}