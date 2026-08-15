using System;
using System.Collections.Generic;
using TestFramework.Azure;
using TestFramework.Core.Environment;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Azure;

internal static class DockerAzurePersistentRootMapper
{
    /// <summary>
    /// Maps persistent requirements onto the components that must be hosted for the whole collection.
    /// </summary>
    /// <param name="environment">The environment holding the Function App definitions, used to derive what a Function App requirement really needs.</param>
    /// <param name="requirements">The persistent requirements declared by the fixture.</param>
    public static IReadOnlyCollection<EnvComponentIdentifier> Map(DockerAzureEnvironment environment, IReadOnlyCollection<EnvironmentRequirement> requirements)
    {
        HashSet<EnvComponentIdentifier> persistentRoots = [DockerAzureEnvironment.NetworkComponentId];
        foreach (EnvironmentRequirement requirement in requirements)
            AddRequirementRoots(environment, persistentRoots, requirement);

        return [.. persistentRoots];
    }

    private static void AddRequirementRoots(DockerAzureEnvironment environment, HashSet<EnvComponentIdentifier> persistentRoots, EnvironmentRequirement requirement)
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
                // Only the emulators this Function App actually binds to. The Function App component itself is PerRun,
                // so it must never become a persistent root — ValidatePersistentRoots would throw. MsSql is pulled in
                // behind Service Bus by PersistentEnvironmentContext.ValidatePersistentClosure.
                foreach (EnvComponentIdentifier component in environment.GetFunctionAppResourceComponents(requirement.ResourceIdentifier))
                    persistentRoots.Add(component);
                return;
            default:
                throw new UnsupportedFrameworkValueException($"Unsupported persistent Azure resource kind '{requirement.ResourceKind}'.");
        }
    }
}