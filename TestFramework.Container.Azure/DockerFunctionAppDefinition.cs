using System;
using TestFramework.Azure.Configuration.SpecificConfigs;
using TestFramework.Azure.Identifier;
using TestFramework.Container.Sources;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Azure;

/// <summary>
/// A Function App run in a Functions host container, with its payload mounted from a declared
/// <see cref="Source"/>.
/// </summary>
public abstract class DockerFunctionAppDefinition : DockerAzureDefinition
{
    private const string SynthesizedBaseUrl = "http://localhost/";
    private const string SynthesizedHostKey = "unused";

    /// <summary>
    /// The Function App identifier steps and configuration refer to.
    /// </summary>
    public abstract FunctionAppIdentifier Identifier { get; }

    /// <summary>
    /// The Functions host image the payload is mounted into.
    /// </summary>
    /// <remarks>
    /// Left at <see cref="DockerAzureDefaults.FunctionAppImage"/>, the image is derived from the payload's target
    /// framework (see <see cref="DockerAzureDefaults.FunctionAppImageFor(string)"/>). Any other value is used as given.
    /// </remarks>
    public virtual string Image => DockerAzureDefaults.FunctionAppImage;

    /// <summary>
    /// Where the payload mounted into the Functions host comes from.
    /// </summary>
    /// <remarks>
    /// There is no default: the payload is declared, not discovered. Because it is mounted into the
    /// Functions host image rather than run as an image of its own, a project source has to publish
    /// on the host: <c>ContainerSource.Project("../App/App.csproj").BuiltOnHost()</c>.
    /// </remarks>
    public virtual ContainerSource Source => throw new FrameworkConfigurationException(
        $"Function App '{Identifier}' declares no container source, so there is no payload to mount.")
    {
        RecoverySteps = ["Override Source with ContainerSource.Project(\"...\").BuiltOnHost() or ContainerSource.Directory(\"...\")."],
    };

    /// <summary>
    /// The Function App configuration this definition declares to the run.
    /// </summary>
    /// <remarks>
    /// The default carries a placeholder base URL, which the address the container publishes replaces, and host
    /// keys set to a fixed unused value. Return <c>null</c> to declare no configuration.
    /// </remarks>
    /// <returns>The configuration, or <c>null</c> for none.</returns>
    protected virtual FunctionAppConfig? CreateDefaultConfig() => new()
    {
        BaseUrl = SynthesizedBaseUrl,
        Code = SynthesizedHostKey,
        AdminCode = SynthesizedHostKey,
    };

    /// <summary>
    /// Binds the Function App to storage, Cosmos and Service Bus definitions and adds app settings. Each binding
    /// also makes the bound definition a dependency. The default binds nothing.
    /// </summary>
    /// <param name="builder">Collects the bindings and settings.</param>
    protected virtual void Configure(DockerFunctionAppBuilder builder)
    {
    }

    internal bool TryCreateDefaultConfig(out FunctionAppConfig config)
    {
        FunctionAppConfig? created = CreateDefaultConfig();
        if (created is null)
        {
            config = default!;
            return false;
        }

        config = created;
        return true;
    }

    internal FunctionAppDefinitionDescriptor CreateDescriptor()
    {
        DockerFunctionAppBuilder builder = new();
        Configure(builder);

        DockerFunctionAppRegistration registration = DockerFunctionAppRegistration.Create(
            Identifier,
            Source,
            registrationBuilder =>
            {
                if (!string.Equals(Image, DockerAzureDefaults.FunctionAppImage, StringComparison.Ordinal))
                    registrationBuilder.WithImage(Image);

                foreach ((string key, string value) in builder.AdditionalSettings)
                    registrationBuilder.WithAppSetting(key, value);
            });

        return new FunctionAppDefinitionDescriptor(GetType(), registration, builder.ServiceBusTopologySources, builder.Dependencies, builder.ResourceBindings);
    }
}
