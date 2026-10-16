using System;
using TestFramework.Azure.Identifier;
using TestFramework.Container.Sources;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Azure;

public sealed partial class DockerAzureEnvironment
{
    /// <summary>
    /// Includes a new instance of <typeparamref name="TDefinition"/>.
    /// </summary>
    /// <typeparam name="TDefinition">The definition to include.</typeparam>
    /// <returns>This environment for chaining.</returns>
    public DockerAzureEnvironment Include<TDefinition>()
        where TDefinition : DockerAzureDefinition, new()
    {
        return Include(new TDefinition());
    }

    /// <summary>
    /// Includes a definition and, transitively, the definitions it depends on. A second definition of a type
    /// already included is ignored.
    /// </summary>
    /// <param name="definition">The definition to include.</param>
    /// <returns>This environment for chaining.</returns>
    /// <exception cref="FrameworkConfigurationException">The definition conflicts with one already included.</exception>
    public DockerAzureEnvironment Include(DockerAzureDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        _includedDefinitions.TryAdd(definition.GetType(), definition);
        _definitionState.AddDefinition(definition);
        return this;
    }

    /// <summary>
    /// Creates an environment with <typeparamref name="TDefinition"/> included.
    /// </summary>
    /// <typeparam name="TDefinition">The definition to include.</typeparam>
    /// <returns>The new environment.</returns>
    public static DockerAzureEnvironment For<TDefinition>()
        where TDefinition : DockerAzureDefinition, new()
    {
        return new DockerAzureEnvironment().Include<TDefinition>();
    }

    /// <remarks>
    /// The payload source is declared, like every definition's. One inline Function App per
    /// environment: a second one deserves a definition class of its own, which is also what gives it
    /// a distinct identity.
    /// </remarks>
    public DockerAzureEnvironment UseFunctionApp(FunctionAppIdentifier identifier, ContainerSource source)
        => this.UseFunctionApp(identifier, source, null, null);

    /// <remarks>
    /// The payload source is declared, like every definition's. One inline Function App per
    /// environment: a second one deserves a definition class of its own, which is also what gives it
    /// a distinct identity.
    /// </remarks>
    public DockerAzureEnvironment UseFunctionApp(FunctionAppIdentifier identifier, ContainerSource source, Action<DockerFunctionAppBuilder>? configure)
        => this.UseFunctionApp(identifier, source, configure, null);

    /// <remarks>
    /// The payload source is declared, like every definition's. One inline Function App per
    /// environment: a second one deserves a definition class of its own, which is also what gives it
    /// a distinct identity.
    /// </remarks>
    public DockerAzureEnvironment UseFunctionApp(FunctionAppIdentifier identifier, ContainerSource source, Action<DockerFunctionAppBuilder>? configure, string? image)
    {
        return Include(new InlineFunctionAppDefinition(identifier, source, configure, image));
    }

    /// <summary>
    /// Creates an environment with one inline Function App and no bindings.
    /// </summary>
    /// <remarks>Uses <c>null</c> for <c>configure</c> and <c>image</c>; a longer overload states them.</remarks>
    /// <param name="identifier">The Function App identifier.</param>
    /// <param name="source">Where the payload mounted into the Functions host comes from.</param>
    /// <returns>The new environment.</returns>
    public static DockerAzureEnvironment ForFunctionApp(FunctionAppIdentifier identifier, ContainerSource source)
        => ForFunctionApp(identifier, source, null, null);

    /// <summary>
    /// Creates an environment with one inline Function App.
    /// </summary>
    /// <remarks>Uses <c>null</c> for <c>image</c>; a longer overload states it.</remarks>
    /// <param name="identifier">The Function App identifier.</param>
    /// <param name="source">Where the payload mounted into the Functions host comes from.</param>
    /// <param name="configure">Binds resources and adds app settings, or <c>null</c> for none.</param>
    /// <returns>The new environment.</returns>
    public static DockerAzureEnvironment ForFunctionApp(FunctionAppIdentifier identifier, ContainerSource source, Action<DockerFunctionAppBuilder>? configure)
        => ForFunctionApp(identifier, source, configure, null);

    /// <summary>
    /// Creates an environment with one inline Function App.
    /// </summary>
    /// <param name="identifier">The Function App identifier.</param>
    /// <param name="source">Where the payload mounted into the Functions host comes from.</param>
    /// <param name="configure">Binds resources and adds app settings, or <c>null</c> for none.</param>
    /// <param name="image">The Functions host image, or <c>null</c> to derive it from the payload's target framework.</param>
    /// <returns>The new environment.</returns>
    public static DockerAzureEnvironment ForFunctionApp(FunctionAppIdentifier identifier, ContainerSource source, Action<DockerFunctionAppBuilder>? configure, string? image)
    {
        return new DockerAzureEnvironment().UseFunctionApp(identifier, source, configure, image);
    }

    /// <summary>
    /// Creates an environment with one inline Function App bound to <typeparamref name="TStorage"/>.
    /// </summary>
    /// <remarks>Uses <c>null</c> for <c>image</c>; a longer overload states it.</remarks>
    /// <typeparam name="TStorage">The storage definition.</typeparam>
    /// <param name="identifier">The Function App identifier.</param>
    /// <param name="source">Where the payload mounted into the Functions host comes from.</param>
    /// <returns>The new environment.</returns>
    public static DockerAzureEnvironment ForFunctionAppWithStorage<TStorage>(FunctionAppIdentifier identifier, ContainerSource source)
        where TStorage : DockerStorageAccountDefinition, new()
        => ForFunctionAppWithStorage<TStorage>(identifier, source, null);

    /// <summary>
    /// Creates an environment with one inline Function App bound to <typeparamref name="TStorage"/>.
    /// </summary>
    /// <typeparam name="TStorage">The storage definition.</typeparam>
    /// <param name="identifier">The Function App identifier.</param>
    /// <param name="source">Where the payload mounted into the Functions host comes from.</param>
    /// <param name="image">The Functions host image, or <c>null</c> to derive it from the payload's target framework.</param>
    /// <returns>The new environment.</returns>
    public static DockerAzureEnvironment ForFunctionAppWithStorage<TStorage>(FunctionAppIdentifier identifier, ContainerSource source, string? image)
        where TStorage : DockerStorageAccountDefinition, new()
    {
        return ForFunctionApp(identifier, source, builder => builder.UseStorageAccount<TStorage>(), image);
    }

    /// <summary>
    /// Creates an environment with one inline Function App bound to <typeparamref name="TStorage"/> and
    /// <typeparamref name="TCosmos"/>.
    /// </summary>
    /// <remarks>Uses <c>null</c> for <c>image</c>; a longer overload states it.</remarks>
    /// <typeparam name="TStorage">The storage definition.</typeparam>
    /// <typeparam name="TCosmos">The Cosmos definition.</typeparam>
    /// <param name="identifier">The Function App identifier.</param>
    /// <param name="source">Where the payload mounted into the Functions host comes from.</param>
    /// <returns>The new environment.</returns>
    public static DockerAzureEnvironment ForFunctionAppWithStorageAndCosmos<TStorage, TCosmos>(FunctionAppIdentifier identifier, ContainerSource source)
        where TStorage : DockerStorageAccountDefinition, new() where TCosmos : DockerCosmosDbDefinition, new()
        => ForFunctionAppWithStorageAndCosmos<TStorage, TCosmos>(identifier, source, null);

    /// <summary>
    /// Creates an environment with one inline Function App bound to <typeparamref name="TStorage"/> and
    /// <typeparamref name="TCosmos"/>.
    /// </summary>
    /// <typeparam name="TStorage">The storage definition.</typeparam>
    /// <typeparam name="TCosmos">The Cosmos definition.</typeparam>
    /// <param name="identifier">The Function App identifier.</param>
    /// <param name="source">Where the payload mounted into the Functions host comes from.</param>
    /// <param name="image">The Functions host image, or <c>null</c> to derive it from the payload's target framework.</param>
    /// <returns>The new environment.</returns>
    public static DockerAzureEnvironment ForFunctionAppWithStorageAndCosmos<TStorage, TCosmos>(FunctionAppIdentifier identifier, ContainerSource source, string? image)
        where TStorage : DockerStorageAccountDefinition, new()
        where TCosmos : DockerCosmosDbDefinition, new()
    {
        return ForFunctionApp(identifier, source, builder => builder
            .UseStorageAccount<TStorage>()
            .UseCosmosDb<TCosmos>(), image);
    }

    /// <summary>
    /// Creates an environment with one inline Function App bound to <typeparamref name="TStorage"/>, triggered by
    /// one <typeparamref name="TServiceBus"/> endpoint and replying to another.
    /// </summary>
    /// <remarks>Uses <c>null</c> for <c>image</c>; a longer overload states it.</remarks>
    /// <typeparam name="TStorage">The storage definition.</typeparam>
    /// <typeparam name="TServiceBus">The Service Bus definition.</typeparam>
    /// <param name="identifier">The Function App identifier.</param>
    /// <param name="source">Where the payload mounted into the Functions host comes from.</param>
    /// <param name="triggerSelector">Picks the trigger endpoint from the definition.</param>
    /// <param name="replySelector">Picks the reply endpoint from the definition.</param>
    /// <returns>The new environment.</returns>
    public static DockerAzureEnvironment ForFunctionAppWithStorageAndServiceBus<TStorage, TServiceBus>(FunctionAppIdentifier identifier, ContainerSource source, Func<TServiceBus, DockerServiceBusEndpoint> triggerSelector, Func<TServiceBus, DockerServiceBusEndpoint> replySelector)
        where TStorage : DockerStorageAccountDefinition, new() where TServiceBus : DockerServiceBusDefinition, new()
        => ForFunctionAppWithStorageAndServiceBus<TStorage, TServiceBus>(identifier, source, triggerSelector, replySelector, null);

    /// <summary>
    /// Creates an environment with one inline Function App bound to <typeparamref name="TStorage"/>, triggered by
    /// one <typeparamref name="TServiceBus"/> endpoint and replying to another.
    /// </summary>
    /// <typeparam name="TStorage">The storage definition.</typeparam>
    /// <typeparam name="TServiceBus">The Service Bus definition.</typeparam>
    /// <param name="identifier">The Function App identifier.</param>
    /// <param name="source">Where the payload mounted into the Functions host comes from.</param>
    /// <param name="triggerSelector">Picks the trigger endpoint from the definition.</param>
    /// <param name="replySelector">Picks the reply endpoint from the definition.</param>
    /// <param name="image">The Functions host image, or <c>null</c> to derive it from the payload's target framework.</param>
    /// <returns>The new environment.</returns>
    public static DockerAzureEnvironment ForFunctionAppWithStorageAndServiceBus<TStorage, TServiceBus>(FunctionAppIdentifier identifier, ContainerSource source, Func<TServiceBus, DockerServiceBusEndpoint> triggerSelector, Func<TServiceBus, DockerServiceBusEndpoint> replySelector, string? image)
        where TStorage : DockerStorageAccountDefinition, new()
        where TServiceBus : DockerServiceBusDefinition, new()
    {
        return ForFunctionApp(identifier, source, builder => builder
            .UseStorageAccount<TStorage>()
            .UseServiceBusTrigger<TServiceBus>(triggerSelector)
            .UseServiceBusReply<TServiceBus>(replySelector), image);
    }

    /// <summary>
    /// Creates an environment with one inline Function App bound to <typeparamref name="TStorage"/> and
    /// <typeparamref name="TCosmos"/>, triggered by one <typeparamref name="TServiceBus"/> endpoint and replying to another.
    /// </summary>
    /// <remarks>Uses <c>null</c> for <c>image</c>; a longer overload states it.</remarks>
    /// <typeparam name="TStorage">The storage definition.</typeparam>
    /// <typeparam name="TCosmos">The Cosmos definition.</typeparam>
    /// <typeparam name="TServiceBus">The Service Bus definition.</typeparam>
    /// <param name="identifier">The Function App identifier.</param>
    /// <param name="source">Where the payload mounted into the Functions host comes from.</param>
    /// <param name="triggerSelector">Picks the trigger endpoint from the definition.</param>
    /// <param name="replySelector">Picks the reply endpoint from the definition.</param>
    /// <returns>The new environment.</returns>
    public static DockerAzureEnvironment ForFunctionAppWithCommonBindings<TStorage, TCosmos, TServiceBus>(FunctionAppIdentifier identifier, ContainerSource source, Func<TServiceBus, DockerServiceBusEndpoint> triggerSelector, Func<TServiceBus, DockerServiceBusEndpoint> replySelector)
        where TStorage : DockerStorageAccountDefinition, new() where TCosmos : DockerCosmosDbDefinition, new() where TServiceBus : DockerServiceBusDefinition, new()
        => ForFunctionAppWithCommonBindings<TStorage, TCosmos, TServiceBus>(identifier, source, triggerSelector, replySelector, null);

    /// <summary>
    /// Creates an environment with one inline Function App bound to <typeparamref name="TStorage"/> and
    /// <typeparamref name="TCosmos"/>, triggered by one <typeparamref name="TServiceBus"/> endpoint and replying to another.
    /// </summary>
    /// <typeparam name="TStorage">The storage definition.</typeparam>
    /// <typeparam name="TCosmos">The Cosmos definition.</typeparam>
    /// <typeparam name="TServiceBus">The Service Bus definition.</typeparam>
    /// <param name="identifier">The Function App identifier.</param>
    /// <param name="source">Where the payload mounted into the Functions host comes from.</param>
    /// <param name="triggerSelector">Picks the trigger endpoint from the definition.</param>
    /// <param name="replySelector">Picks the reply endpoint from the definition.</param>
    /// <param name="image">The Functions host image, or <c>null</c> to derive it from the payload's target framework.</param>
    /// <returns>The new environment.</returns>
    public static DockerAzureEnvironment ForFunctionAppWithCommonBindings<TStorage, TCosmos, TServiceBus>(FunctionAppIdentifier identifier, ContainerSource source, Func<TServiceBus, DockerServiceBusEndpoint> triggerSelector, Func<TServiceBus, DockerServiceBusEndpoint> replySelector, string? image)
        where TStorage : DockerStorageAccountDefinition, new()
        where TCosmos : DockerCosmosDbDefinition, new()
        where TServiceBus : DockerServiceBusDefinition, new()
    {
        return ForFunctionApp(identifier, source, builder => builder
            .UseStorageAccount<TStorage>()
            .UseCosmosDb<TCosmos>()
            .UseServiceBusTrigger<TServiceBus>(triggerSelector)
            .UseServiceBusReply<TServiceBus>(replySelector), image);
    }

    private sealed class InlineFunctionAppDefinition(FunctionAppIdentifier identifier, ContainerSource source, Action<DockerFunctionAppBuilder>? configure, string? image) : DockerFunctionAppDefinition
    {
        public override FunctionAppIdentifier Identifier => identifier;

        public override ContainerSource Source => source;

        public override string Image => string.IsNullOrWhiteSpace(image) ? base.Image : image!;

        protected override void Configure(DockerFunctionAppBuilder builder)
        {
            configure?.Invoke(builder);
        }
    }
}