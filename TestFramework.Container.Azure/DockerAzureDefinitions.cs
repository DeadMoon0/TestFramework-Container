using TestFramework.Azure.Identifier;
using TestFramework.Core.Environment;

namespace TestFramework.Container.Azure;

public abstract class DockerAzureDefinition
{
    protected virtual void ConfigureDependencies(DockerAzureDependencyBuilder dependencies)
    {
    }

    internal IReadOnlyCollection<Type> GetDependencyDefinitionTypes()
    {
        DockerAzureDependencyBuilder dependencies = new();
        ConfigureDependencies(dependencies);
        return dependencies.DefinitionTypes;
    }
}

public abstract class DockerAzureInfrastructureDefinition : DockerAzureDefinition
{
    public virtual string? AzuriteImage => null;

    public virtual string? CosmosDbImage => null;

    public virtual string? MsSqlImage => null;

    public virtual string? ServiceBusImage => null;

    public virtual string? MsSqlPassword => null;

    public virtual string? ServiceBusTopologyConfigPath => null;
}

public abstract class DockerStorageDefinition : DockerAzureDefinition
{
    public abstract StorageAccountIdentifier Identifier { get; }
}

public abstract class DockerCosmosDefinition : DockerAzureDefinition
{
    public abstract CosmosContainerIdentifier Identifier { get; }

    public virtual Type? ModelType => null;
}

public abstract class DockerCosmosDefinition<TDocument> : DockerCosmosDefinition
{
    public sealed override Type ModelType => typeof(TDocument);
}

public abstract class DockerSqlDefinition : DockerAzureDefinition
{
    public abstract SqlDatabaseIdentifier Identifier { get; }
}

public abstract class DockerServiceBusDefinition : DockerAzureDefinition
{
    public abstract ServiceBusIdentifier Identifier { get; }

    public virtual string TopologyConfigPath => DockerAzureDefaults.ServiceBusTopologyConfigPath;
}

public abstract class DockerFunctionAppDefinition : DockerAzureDefinition
{
    public abstract FunctionAppIdentifier Identifier { get; }

    public virtual string Image => DockerAzureDefaults.FunctionAppImage;

    protected virtual void Configure(DockerFunctionAppBuilder builder)
    {
    }

    internal FunctionAppDefinitionDescriptor CreateDescriptor()
    {
        DockerFunctionAppBuilder builder = new();
        Configure(builder);

        DockerFunctionAppRegistration registration = DockerFunctionAppRegistration.Create(
            Identifier,
            FunctionType,
            registrationBuilder =>
            {
                if (!string.Equals(Image, DockerAzureDefaults.FunctionAppImage, StringComparison.Ordinal))
                    registrationBuilder.WithImage(Image);

                if (builder.StorageIdentifier is not null)
                {
                    registrationBuilder.UseStorage(
                        builder.StorageIdentifier,
                        builder.StorageConnectionSettingName,
                        builder.HostStorageSettingName,
                        builder.StorageTableNameSettingName);
                }

                if (builder.CosmosIdentifier is not null)
                {
                    registrationBuilder.UseCosmos(
                        builder.CosmosIdentifier,
                        builder.CosmosConnectionSettingName,
                        builder.CosmosDatabaseSettingName,
                        builder.CosmosContainerSettingName);
                }

                if (builder.ServiceBusTriggerIdentifier is not null)
                {
                    registrationBuilder.UseServiceBusTrigger(
                        builder.ServiceBusTriggerIdentifier,
                        builder.ServiceBusTriggerConnectionSettingName,
                        builder.ServiceBusTriggerEntitySettingName,
                        builder.ServiceBusTriggerSubscriptionSettingName);
                }

                if (builder.ServiceBusReplyIdentifier is not null)
                {
                    registrationBuilder.UseServiceBusReply(
                        builder.ServiceBusReplyIdentifier,
                        builder.ServiceBusReplyConnectionSettingName,
                        builder.ServiceBusReplyEntitySettingName);
                }

                foreach ((string key, string value) in builder.AdditionalSettings)
                    registrationBuilder.WithAppSetting(key, value);
            });

        return new FunctionAppDefinitionDescriptor(registration, builder.ServiceBusTopologyPaths, builder.DependencyDefinitionTypes);
    }

    internal abstract Type FunctionType { get; }
}

public abstract class DockerFunctionAppDefinition<TFunctionApp> : DockerFunctionAppDefinition
{
    internal sealed override Type FunctionType => typeof(TFunctionApp);
}

public sealed class DockerAzureDependencyBuilder
{
    internal HashSet<Type> DefinitionTypes { get; } = [];

    public DockerAzureDependencyBuilder Include<TDefinition>()
        where TDefinition : DockerAzureDefinition, new()
    {
        DefinitionTypes.Add(typeof(TDefinition));
        return this;
    }
}

public sealed class DockerFunctionAppBuilder
{
    internal StorageAccountIdentifier? StorageIdentifier { get; private set; }
    internal string StorageConnectionSettingName { get; private set; } = "StorageAccountConnectionString";
    internal string HostStorageSettingName { get; private set; } = "AzureWebJobsStorage";
    internal string? StorageTableNameSettingName { get; private set; }
    internal CosmosContainerIdentifier? CosmosIdentifier { get; private set; }
    internal string CosmosConnectionSettingName { get; private set; } = "CosmosDbConnectionString";
    internal string CosmosDatabaseSettingName { get; private set; } = "CosmosDatabaseName";
    internal string CosmosContainerSettingName { get; private set; } = "CosmosContainerName";
    internal ServiceBusIdentifier? ServiceBusTriggerIdentifier { get; private set; }
    internal string ServiceBusTriggerConnectionSettingName { get; private set; } = "ServiceBusTriggerConnection";
    internal string ServiceBusTriggerEntitySettingName { get; private set; } = "ServiceBusTriggerTopicName";
    internal string? ServiceBusTriggerSubscriptionSettingName { get; private set; } = "ServiceBusTriggerSubscriptionName";
    internal ServiceBusIdentifier? ServiceBusReplyIdentifier { get; private set; }
    internal string ServiceBusReplyConnectionSettingName { get; private set; } = "ServiceBusReplyConnectionString";
    internal string ServiceBusReplyEntitySettingName { get; private set; } = "ServiceBusReplyTopicName";
    internal Dictionary<string, string> AdditionalSettings { get; } = [];
    internal HashSet<string> ServiceBusTopologyPaths { get; } = [];
    internal HashSet<Type> DependencyDefinitionTypes { get; } = [];

    public DockerFunctionAppBuilder UseStorage<TStorage>(string connectionSettingName = "StorageAccountConnectionString", string hostStorageSettingName = "AzureWebJobsStorage", string? tableNameSettingName = null)
        where TStorage : DockerStorageDefinition, new()
    {
        TStorage definition = new();
        DependencyDefinitionTypes.Add(typeof(TStorage));
        StorageIdentifier = definition.Identifier;
        StorageConnectionSettingName = connectionSettingName;
        HostStorageSettingName = hostStorageSettingName;
        StorageTableNameSettingName = tableNameSettingName;
        return this;
    }

    public DockerFunctionAppBuilder UseCosmos<TCosmos>(string connectionSettingName = "CosmosDbConnectionString", string databaseSettingName = "CosmosDatabaseName", string containerSettingName = "CosmosContainerName")
        where TCosmos : DockerCosmosDefinition, new()
    {
        TCosmos definition = new();
        DependencyDefinitionTypes.Add(typeof(TCosmos));
        CosmosIdentifier = definition.Identifier;
        CosmosConnectionSettingName = connectionSettingName;
        CosmosDatabaseSettingName = databaseSettingName;
        CosmosContainerSettingName = containerSettingName;
        return this;
    }

    public DockerFunctionAppBuilder UseServiceBusTrigger<TServiceBus>(string connectionSettingName = "ServiceBusTriggerConnection", string entitySettingName = "ServiceBusTriggerTopicName", string? subscriptionSettingName = "ServiceBusTriggerSubscriptionName")
        where TServiceBus : DockerServiceBusDefinition, new()
    {
        TServiceBus definition = new();
        DependencyDefinitionTypes.Add(typeof(TServiceBus));
        ServiceBusTriggerIdentifier = definition.Identifier;
        ServiceBusTriggerConnectionSettingName = connectionSettingName;
        ServiceBusTriggerEntitySettingName = entitySettingName;
        ServiceBusTriggerSubscriptionSettingName = subscriptionSettingName;
        if (!string.Equals(definition.TopologyConfigPath, DockerAzureDefaults.ServiceBusTopologyConfigPath, StringComparison.OrdinalIgnoreCase))
            ServiceBusTopologyPaths.Add(definition.TopologyConfigPath);
        return this;
    }

    public DockerFunctionAppBuilder UseServiceBusReply<TServiceBus>(string connectionSettingName = "ServiceBusReplyConnectionString", string entitySettingName = "ServiceBusReplyTopicName")
        where TServiceBus : DockerServiceBusDefinition, new()
    {
        TServiceBus definition = new();
        DependencyDefinitionTypes.Add(typeof(TServiceBus));
        ServiceBusReplyIdentifier = definition.Identifier;
        ServiceBusReplyConnectionSettingName = connectionSettingName;
        ServiceBusReplyEntitySettingName = entitySettingName;
        if (!string.Equals(definition.TopologyConfigPath, DockerAzureDefaults.ServiceBusTopologyConfigPath, StringComparison.OrdinalIgnoreCase))
            ServiceBusTopologyPaths.Add(definition.TopologyConfigPath);
        return this;
    }

    public DockerFunctionAppBuilder WithAppSetting(string key, string value)
    {
        AdditionalSettings[key] = value;
        return this;
    }
}

public static class DockerAzureDefaults
{
    public const string FunctionAppImage = "mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated8.0";
    public const string MsSqlImage = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";
    public const string AzuriteImage = "mcr.microsoft.com/azure-storage/azurite:3.33.0";
    public const string CosmosDbImage = "mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview";
    public const string ServiceBusImage = "mcr.microsoft.com/azure-messaging/servicebus-emulator:latest";
    public const string MsSqlPassword = "TestFramework_Container1!";
    public static readonly string ServiceBusTopologyConfigPath = Path.Combine("Configurations", "ServiceBus", "config.json");
}

internal sealed record FunctionAppDefinitionDescriptor(
    DockerFunctionAppRegistration Registration,
    IReadOnlyCollection<string> ServiceBusTopologyPaths,
    IReadOnlyCollection<Type> DependencyDefinitionTypes);

internal sealed class DockerAzureDefinitionState
{
    private readonly HashSet<Type> _definitionTypes = [];

    public HashSet<EnvComponentIdentifier> RequiredComponents { get; } = [];
    public HashSet<FunctionAppIdentifier> RequiredFunctionAppIdentifiers { get; } = [];
    public HashSet<ServiceBusIdentifier> RequiredServiceBusIdentifiers { get; } = [];
    public HashSet<StorageAccountIdentifier> RequiredStorageIdentifiers { get; } = [];
    public HashSet<CosmosContainerIdentifier> RequiredCosmosIdentifiers { get; } = [];
    public HashSet<SqlDatabaseIdentifier> RequiredSqlIdentifiers { get; } = [];
    public List<DockerFunctionAppRegistration> FunctionApps { get; } = [];
    public Dictionary<string, Type> CosmosModelTypes { get; } = new(StringComparer.Ordinal);
    public string? ServiceBusTopologyConfigPath { get; private set; }
    public string? AzuriteImage { get; private set; }
    public string? CosmosDbImage { get; private set; }
    public string? MsSqlImage { get; private set; }
    public string? ServiceBusImage { get; private set; }
    public string? MsSqlPassword { get; private set; }

    public void AddDefinition(DockerAzureDefinition definition)
    {
        Type definitionType = definition.GetType();
        if (!_definitionTypes.Add(definitionType))
            return;

        foreach (Type dependencyType in definition.GetDependencyDefinitionTypes())
            AddDependencyDefinition(dependencyType, definitionType);

        switch (definition)
        {
            case DockerAzureInfrastructureDefinition infrastructure:
                AzuriteImage = ResolveOverride(AzuriteImage, infrastructure.AzuriteImage, nameof(DockerAzureInfrastructureDefinition.AzuriteImage));
                CosmosDbImage = ResolveOverride(CosmosDbImage, infrastructure.CosmosDbImage, nameof(DockerAzureInfrastructureDefinition.CosmosDbImage));
                MsSqlImage = ResolveOverride(MsSqlImage, infrastructure.MsSqlImage, nameof(DockerAzureInfrastructureDefinition.MsSqlImage));
                ServiceBusImage = ResolveOverride(ServiceBusImage, infrastructure.ServiceBusImage, nameof(DockerAzureInfrastructureDefinition.ServiceBusImage));
                MsSqlPassword = ResolveOverride(MsSqlPassword, infrastructure.MsSqlPassword, nameof(DockerAzureInfrastructureDefinition.MsSqlPassword));
                if (!string.IsNullOrWhiteSpace(infrastructure.ServiceBusTopologyConfigPath))
                    SetServiceBusTopologyConfigPath(infrastructure.ServiceBusTopologyConfigPath);
                break;
            case DockerStorageDefinition storage:
                RequiredStorageIdentifiers.Add(storage.Identifier);
                break;
            case DockerCosmosDefinition cosmos:
                RequiredCosmosIdentifiers.Add(cosmos.Identifier);
                if (cosmos.ModelType is not null)
                    AddCosmosModelType(cosmos.Identifier, cosmos.ModelType);
                break;
            case DockerSqlDefinition sql:
                RequiredSqlIdentifiers.Add(sql.Identifier);
                break;
            case DockerServiceBusDefinition serviceBus:
                RequiredServiceBusIdentifiers.Add(serviceBus.Identifier);
                if (!string.Equals(serviceBus.TopologyConfigPath, DockerAzureDefaults.ServiceBusTopologyConfigPath, StringComparison.OrdinalIgnoreCase))
                    SetServiceBusTopologyConfigPath(serviceBus.TopologyConfigPath);
                break;
            case DockerFunctionAppDefinition functionApp:
                RequiredFunctionAppIdentifiers.Add(functionApp.Identifier);
                FunctionAppDefinitionDescriptor descriptor = functionApp.CreateDescriptor();
                foreach (Type dependencyType in descriptor.DependencyDefinitionTypes)
                    AddDependencyDefinition(dependencyType, definitionType);
                AddFunctionAppRegistration(descriptor.Registration);
                foreach (string path in descriptor.ServiceBusTopologyPaths)
                    SetServiceBusTopologyConfigPath(path);
                break;
            default:
                throw new InvalidOperationException($"Unsupported Docker Azure definition type '{definition.GetType().FullName}'.");
        }
    }

    private void AddDependencyDefinition(Type dependencyType, Type ownerType)
    {
        if (!typeof(DockerAzureDefinition).IsAssignableFrom(dependencyType))
            throw new InvalidOperationException($"Dependency type '{dependencyType.FullName}' declared by '{ownerType.FullName}' is not a Docker Azure definition.");

        if (dependencyType.IsAbstract)
            throw new InvalidOperationException($"Dependency type '{dependencyType.FullName}' declared by '{ownerType.FullName}' must be a concrete Docker Azure definition.");

        if (Activator.CreateInstance(dependencyType) is not DockerAzureDefinition dependencyDefinition)
            throw new InvalidOperationException($"Dependency type '{dependencyType.FullName}' declared by '{ownerType.FullName}' could not be constructed.");

        AddDefinition(dependencyDefinition);
    }

    private void AddFunctionAppRegistration(DockerFunctionAppRegistration registration)
    {
        DockerFunctionAppRegistration? existing = FunctionApps.FirstOrDefault(x => string.Equals(x.Identifier, registration.Identifier, StringComparison.Ordinal));
        if (existing is not null)
        {
            if (existing.FunctionType != registration.FunctionType)
                throw new InvalidOperationException($"Docker Function App identifier '{registration.Identifier}' was configured for multiple function types.");

            return;
        }

        FunctionApps.Add(registration);
    }

    private void AddCosmosModelType(CosmosContainerIdentifier identifier, Type modelType)
    {
        if (CosmosModelTypes.TryGetValue(identifier, out Type? existing) && existing != modelType)
            throw new InvalidOperationException($"Cosmos identifier '{identifier}' was configured for multiple model types.");

        CosmosModelTypes[identifier] = modelType;
    }

    private void SetServiceBusTopologyConfigPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (ServiceBusTopologyConfigPath is null)
        {
            ServiceBusTopologyConfigPath = path;
            return;
        }

        if (!string.Equals(ServiceBusTopologyConfigPath, path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Multiple Service Bus topology paths were configured: '{ServiceBusTopologyConfigPath}' and '{path}'.");
    }

    private static string? ResolveOverride(string? current, string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return current;

        if (current is null)
            return value;

        if (!string.Equals(current, value, StringComparison.Ordinal))
            throw new InvalidOperationException($"Multiple values were configured for {propertyName}: '{current}' and '{value}'.");

        return current;
    }
}