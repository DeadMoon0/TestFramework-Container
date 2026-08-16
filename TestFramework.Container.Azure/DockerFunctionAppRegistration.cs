using System;
using System.Collections.Generic;
using TestFramework.Container.Sources;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Azure;

/// <summary>
/// One Function App the environment runs, and where its payload comes from.
/// </summary>
public sealed class DockerFunctionAppRegistration
{
    private DockerFunctionAppRegistration(string identifier, Type? functionType, ContainerSource source)
    {
        Identifier = identifier;
        FunctionType = functionType;
        Source = source;
    }

    /// <summary>
    /// The identifier the environment and the configuration store share.
    /// </summary>
    public string Identifier { get; }

    /// <summary>
    /// A type from the Function App assembly, when the payload is described by one.
    /// </summary>
    /// <remarks>
    /// Null for a Function App that declares its <see cref="Source"/> as a project or a directory
    /// instead, which needs no reference to the application from the test project at all.
    /// </remarks>
    public Type? FunctionType { get; }

    /// <summary>
    /// Where the payload mounted into the Functions host comes from.
    /// </summary>
    public ContainerSource Source { get; }

    /// <summary>
    /// The Functions host image the payload is mounted into.
    /// </summary>
    public string Image { get; private set; } = DockerAzureDefaults.FunctionAppImage;

    /// <summary>
    /// Whether the host image was named by the caller rather than left to be worked out.
    /// </summary>
    /// <remarks>
    /// A declared image is used exactly as given: the caller knows something this does not, and
    /// second-guessing them would be worse than being wrong. Only an image nobody chose is derived
    /// from the payload's framework.
    /// </remarks>
    internal bool ImageWasDeclared { get; private set; }

    internal Dictionary<string, string> AdditionalSettings { get; } = [];

    /// <summary>
    /// Registers a Function App described by a type from its assembly.
    /// </summary>
    /// <typeparam name="TFunctionApp">A type from the Function App assembly.</typeparam>
    /// <param name="identifier">The identifier the configuration store uses.</param>
    /// <param name="configure">Optional extra registration settings.</param>
    public static DockerFunctionAppRegistration Create<TFunctionApp>(string identifier = "Default", Action<Builder>? configure = null)
        => Create(identifier, typeof(TFunctionApp), ContainerSource.EntryPoint(typeof(TFunctionApp)), configure);

    internal static DockerFunctionAppRegistration Create(string identifier, Type? functionType, ContainerSource source, Action<Builder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        DockerFunctionAppRegistration registration = new(identifier, functionType, source);
        Builder builder = new(registration);
        configure?.Invoke(builder);
        return registration;
    }

    /// <summary>
    /// Describes the payload for log and error output, whether or not a type backs it.
    /// </summary>
    internal string DescribeSource()
        => FunctionType is { } type ? $"type '{type.FullName ?? type.Name}'" : Source.ToString() ?? Source.GetType().Name;

    /// <summary>
    /// Optional registration settings.
    /// </summary>
    /// <param name="registration">The registration being built.</param>
    public sealed class Builder(DockerFunctionAppRegistration registration)
    {
        /// <summary>
        /// Overrides the Functions host image the payload is mounted into.
        /// </summary>
        /// <param name="image">The image reference.</param>
        public Builder WithImage(string image)
        {
            if (string.IsNullOrWhiteSpace(image))
                throw new FrameworkConfigurationException($"Function App '{registration.Identifier}' was given an empty host image.");

            registration.Image = image;
            registration.ImageWasDeclared = true;
            return this;
        }

        /// <summary>
        /// Adds an application setting the Functions host starts with.
        /// </summary>
        /// <param name="key">The setting name.</param>
        /// <param name="value">The setting value.</param>
        public Builder WithAppSetting(string key, string value)
        {
            registration.AdditionalSettings[key] = value;
            return this;
        }
    }
}
