using System;
using System.Collections.Generic;
using TestFramework.Core.Environment;

namespace TestFramework.Container.Azure;

internal sealed record FunctionAppDefinitionDescriptor(
    Type DefinitionType,
    DockerFunctionAppRegistration Registration,
    IReadOnlyCollection<ServiceBusTopologySource> ServiceBusTopologySources,
    IReadOnlyCollection<ComponentDependency> Dependencies,
    IReadOnlyCollection<FunctionAppResourceBinding> ResourceBindings);
