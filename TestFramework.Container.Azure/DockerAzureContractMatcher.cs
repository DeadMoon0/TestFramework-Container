using TestFramework.Azure.Contracts;
using TestFramework.Core.Environment;

namespace TestFramework.Container.Azure;

/// <summary>
/// Matches Azure-specific resource contracts using exact identity fields plus optional narrowing fields.
/// </summary>
public static class DockerAzureContractMatcher
{
    public static bool IsMatch(IEnvironmentResourceContract requirement, IEnvironmentResourceContract provider)
    {
        return requirement switch
        {
            BlobContainerContract requiredBlob when provider is BlobContainerContract providedBlob => IsMatch(requiredBlob, providedBlob),
            CosmosContract requiredCosmos when provider is CosmosContract providedCosmos => IsMatch(requiredCosmos, providedCosmos),
            SqlDatabaseContract requiredSql when provider is SqlDatabaseContract providedSql => IsMatch(requiredSql, providedSql),
            ServiceBusEndpointContract requiredServiceBus when provider is ServiceBusEndpointContract providedServiceBus => IsMatch(requiredServiceBus, providedServiceBus),
            _ => false
        };
    }

    private static bool IsMatch(BlobContainerContract requirement, BlobContainerContract provider)
    {
        return provider.StorageIdentifier == requirement.StorageIdentifier
            && string.Equals(provider.ContainerName, requirement.ContainerName, StringComparison.Ordinal)
            && MatchesOptional(requirement.BindingName, provider.BindingName)
            && SupportsAccess(provider.AccessMode, requirement.AccessMode);
    }

    private static bool IsMatch(CosmosContract requirement, CosmosContract provider)
    {
        return provider.CosmosIdentifier == requirement.CosmosIdentifier
            && string.Equals(provider.DatabaseName, requirement.DatabaseName, StringComparison.Ordinal)
            && string.Equals(provider.ContainerName, requirement.ContainerName, StringComparison.Ordinal)
            && MatchesOptional(requirement.PartitionKeyPath, provider.PartitionKeyPath)
            && MatchesOptional(requirement.BindingName, provider.BindingName);
    }

    private static bool IsMatch(SqlDatabaseContract requirement, SqlDatabaseContract provider)
    {
        return provider.SqlIdentifier == requirement.SqlIdentifier
            && string.Equals(provider.DatabaseName, requirement.DatabaseName, StringComparison.Ordinal)
            && MatchesOptional(requirement.SchemaName, provider.SchemaName)
            && MatchesOptional(requirement.BindingName, provider.BindingName);
    }

    private static bool IsMatch(ServiceBusEndpointContract requirement, ServiceBusEndpointContract provider)
    {
        if (provider.ServiceBusIdentifier != requirement.ServiceBusIdentifier
            || provider.EndpointKind != requirement.EndpointKind
            || !string.Equals(provider.EntityName, requirement.EntityName, StringComparison.Ordinal)
            || !MatchesOptional(requirement.BindingName, provider.BindingName))
        {
            return false;
        }

        if (requirement.EndpointKind == ServiceBusEndpointKind.TopicSubscription)
            return MatchesRequired(requirement.SubscriptionName, provider.SubscriptionName);

        return true;
    }

    private static bool SupportsAccess(BlobAccessMode provider, BlobAccessMode requirement)
    {
        return provider switch
        {
            BlobAccessMode.ReadWrite => true,
            BlobAccessMode.Read => requirement == BlobAccessMode.Read,
            BlobAccessMode.Write => requirement == BlobAccessMode.Write,
            _ => false
        };
    }

    private static bool MatchesOptional(string? requiredValue, string? providedValue)
    {
        return string.IsNullOrWhiteSpace(requiredValue)
            || string.Equals(requiredValue, providedValue, StringComparison.Ordinal);
    }

    private static bool MatchesRequired(string? requiredValue, string? providedValue)
    {
        return !string.IsNullOrWhiteSpace(requiredValue)
            && string.Equals(requiredValue, providedValue, StringComparison.Ordinal);
    }
}
