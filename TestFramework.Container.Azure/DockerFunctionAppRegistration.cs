using System;
using System.Collections.Generic;
namespace TestFramework.Container.Azure;

public sealed class DockerFunctionAppRegistration
{
    private DockerFunctionAppRegistration(string identifier, Type functionType)
    {
        Identifier = identifier;
        FunctionType = functionType;
    }

    public string Identifier { get; }

    public Type FunctionType { get; }

    public string Image { get; private set; } = "mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated8.0";

    internal Dictionary<string, string> AdditionalSettings { get; } = [];

    public static DockerFunctionAppRegistration Create<TFunctionApp>(string identifier = "Default", Action<Builder>? configure = null)
    {
        DockerFunctionAppRegistration registration = new(identifier, typeof(TFunctionApp));
        Builder builder = new(registration);
        configure?.Invoke(builder);
        return registration;
    }

    internal static DockerFunctionAppRegistration Create(string identifier, Type functionType, Action<Builder>? configure = null)
    {
        DockerFunctionAppRegistration registration = new(identifier, functionType);
        Builder builder = new(registration);
        configure?.Invoke(builder);
        return registration;
    }

    public sealed class Builder(DockerFunctionAppRegistration registration)
    {
        public Builder WithImage(string image)
        {
            registration.Image = image;
            return this;
        }

        public Builder WithAppSetting(string key, string value)
        {
            registration.AdditionalSettings[key] = value;
            return this;
        }
    }
}