using System;
using System.Collections.Generic;
using TestFramework.Azure;
using TestFramework.Core.Environment;

namespace TestFramework.Container.Azure;

internal static class DockerAzurePersistentRootMapper
{
    public static IReadOnlyCollection<EnvComponentIdentifier> Map(IReadOnlyCollection<EnvironmentRequirement> requirements)
    {
        HashSet<EnvComponentIdentifier> persistentRoots = [DockerAzureEnvironment.NetworkComponentId];
        foreach (EnvironmentRequirement requirement in requirements)
            AddRequirementRoots(persistentRoots, requirement);

        return [.. persistentRoots];
    }

    private static void AddRequirementRoots(HashSet<EnvComponentIdentifier> persistentRoots, EnvironmentRequirement requirement)
    {
        switch (requirement.ResourceKind)
        {
            case AzureEnvironmentResourceKinds.Storage:
                persistentRoots.Add(DockerAzureEnvironment.AzuriteComponentId);
                return;
            case AzureEnvironmentResourceKinds.Cosmos:
                persistentRoots.Add(DockerAzureEnvironment.CosmosDbComponentId);
                return;
            case AzureEnvironmentResourceKinds.Sql:
                persistentRoots.Add(DockerAzureEnvironment.MsSqlComponentId);
                return;
            case AzureEnvironmentResourceKinds.ServiceBus:
                persistentRoots.Add(DockerAzureEnvironment.ServiceBusComponentId);
                return;
            case AzureEnvironmentResourceKinds.FunctionApp:
                persistentRoots.Add(DockerAzureEnvironment.AzuriteComponentId);
                persistentRoots.Add(DockerAzureEnvironment.CosmosDbComponentId);
                persistentRoots.Add(DockerAzureEnvironment.ServiceBusComponentId);
                persistentRoots.Add(DockerAzureEnvironment.MsSqlComponentId);
                return;
            default:
                throw new InvalidOperationException($"Unsupported persistent Azure resource kind '{requirement.ResourceKind}'.");
        }
    }
}