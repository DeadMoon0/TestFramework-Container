using TestFramework.Core.Environment;
using TestFramework.Core.Exceptions;

namespace TestFramework.Container.Web.Components;

/// <summary>
/// Shared behaviour for the components of a container-backed web environment.
/// </summary>
internal abstract class WebEnvComponentBase : EnvComponent
{
    /// <summary>
    /// Returns the environment as the provider this component belongs to.
    /// </summary>
    /// <param name="environment">The environment the component was created by, possibly wrapped.</param>
    /// <exception cref="FrameworkStateException">The component was created by a different provider.</exception>
    protected DockerWebEnvironment GetWebEnvironment(IEnvironmentProvider environment)
    {
        if (environment is DockerWebEnvironment webEnvironment)
            return webEnvironment;

        if (environment is IEnvironmentProviderProxy proxy)
            return GetWebEnvironment(proxy.InnerEnvironment);

        throw new FrameworkStateException($"Environment component '{Id}' requires {nameof(DockerWebEnvironment)}.");
    }
}
