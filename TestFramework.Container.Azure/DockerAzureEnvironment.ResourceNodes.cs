using System;
using System.Collections.Generic;
using TestFramework.Azure.Configuration;
using TestFramework.Config.Configuration;
using TestFramework.Core.Environment.Graph;

namespace TestFramework.Container.Azure;

public sealed partial class DockerAzureEnvironment
{
    /// <inheritdoc />
    public string SourceName => "Docker definition";

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// A definition may say what its resource is - the database a Cosmos container lives in, the blob
    /// container a storage account serves - and for a resource that only exists in a container, that is
    /// the only place the name can come from: nobody wrote a configuration entry for a database that is
    /// created when the run starts. These reach a run the same way a file's entries do, so a step asking
    /// where something is never learns which of the two answered.
    /// </para>
    /// <para>
    /// They lose to a configuration entry naming the same resource, which is the composition order rather
    /// than a rule here: a default is for what nobody wrote down, and somebody who wrote it down meant it.
    /// The address in these is a placeholder and is meant to be - where a container ends up is published
    /// while it starts, and a published value outranks any declaration.
    /// </para>
    /// </remarks>
    public IReadOnlyList<ResourceNode> Nodes => _definitionNodes.Nodes;

    private readonly DefinitionDefaults _definitionNodes;

    /// <summary>
    /// The definitions' own configuration, as declared resources.
    /// </summary>
    /// <remarks>
    /// Read when the graph is first composed rather than at construction: definitions arrive through
    /// <c>Include</c> afterwards, so an environment that enumerated them now would declare nothing.
    /// </remarks>
    private sealed class DefinitionDefaults(DockerAzureDefinitionState definitions) : DeclaredNodeSource
    {
        public override string SourceName => "Docker definition";

        protected override IEnumerable<DeclaredResource> Declarations
        {
            get
            {
                foreach ((Type configType, string identifier, object config) in definitions.DefaultConfigs())
                {
                    // The package that owns the record owns the mapping too. Writing it again here would be
                    // a second opinion about which values an entry holds, and the two would only differ in
                    // the cases nobody thought to check.
                    IConfigShape shape = AzureConfigShapes.For(configType);
                    yield return new DeclaredResource { Kind = shape.Kind, Identifier = identifier, Values = shape.Values(config, identifier), DeclaredBy = this.SourceName };
                }
            }
        }
    }
}