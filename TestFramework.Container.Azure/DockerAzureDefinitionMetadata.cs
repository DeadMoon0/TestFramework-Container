using System;
using System.Collections.Generic;
using TestFramework.Core.Environment;

namespace TestFramework.Container.Azure;

internal sealed record DockerAzureDefinitionMetadata(
    DockerAzureDefinition Definition,
    Type DefinitionType,
    string RealizedIdentity,
    IReadOnlyCollection<ComponentDependency> Dependencies,
    IReadOnlyCollection<IEnvironmentResourceContract> Provides,
    IReadOnlyCollection<IEnvironmentResourceContract> Requires);
