using TestFramework.Azure.Identifier;

namespace TestFramework.Container.AzureDocker;

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

    internal string? StorageIdentifier { get; private set; }

    internal string? HostStorageSettingName { get; private set; }

    internal string? StorageConnectionSettingName { get; private set; }

    internal string? StorageTableNameSettingName { get; private set; }

    internal string? CosmosIdentifier { get; private set; }

    internal string? CosmosConnectionSettingName { get; private set; }

    internal string? CosmosDatabaseSettingName { get; private set; }

    internal string? CosmosContainerSettingName { get; private set; }

    internal string? ServiceBusTriggerIdentifier { get; private set; }

    internal string? ServiceBusTriggerConnectionSettingName { get; private set; }

    internal string? ServiceBusTriggerEntitySettingName { get; private set; }

    internal string? ServiceBusTriggerSubscriptionSettingName { get; private set; }

    internal string? ServiceBusReplyIdentifier { get; private set; }

    internal string? ServiceBusReplyConnectionSettingName { get; private set; }

    internal string? ServiceBusReplyEntitySettingName { get; private set; }

    internal Dictionary<string, string> AdditionalSettings { get; } = [];

    public static DockerFunctionAppRegistration Create<TFunctionApp>(string identifier = "Default", Action<Builder>? configure = null)
    {
        DockerFunctionAppRegistration registration = new(identifier, typeof(TFunctionApp));
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

        public Builder UseStorage(StorageAccountIdentifier identifier, string connectionSettingName = "StorageAccountConnectionString", string hostStorageSettingName = "AzureWebJobsStorage", string? tableNameSettingName = null)
        {
            registration.StorageIdentifier = identifier;
            registration.StorageConnectionSettingName = connectionSettingName;
            registration.HostStorageSettingName = hostStorageSettingName;
            registration.StorageTableNameSettingName = tableNameSettingName;
            return this;
        }

        public Builder UseCosmos(CosmosContainerIdentifier identifier, string connectionSettingName = "CosmosDbConnectionString", string databaseSettingName = "CosmosDatabaseName", string containerSettingName = "CosmosContainerName")
        {
            registration.CosmosIdentifier = identifier;
            registration.CosmosConnectionSettingName = connectionSettingName;
            registration.CosmosDatabaseSettingName = databaseSettingName;
            registration.CosmosContainerSettingName = containerSettingName;
            return this;
        }

        public Builder UseServiceBusTrigger(ServiceBusIdentifier identifier, string connectionSettingName = "ServiceBusTriggerConnection", string entitySettingName = "ServiceBusTriggerTopicName", string? subscriptionSettingName = "ServiceBusTriggerSubscriptionName")
        {
            registration.ServiceBusTriggerIdentifier = identifier;
            registration.ServiceBusTriggerConnectionSettingName = connectionSettingName;
            registration.ServiceBusTriggerEntitySettingName = entitySettingName;
            registration.ServiceBusTriggerSubscriptionSettingName = subscriptionSettingName;
            return this;
        }

        public Builder UseServiceBusReply(ServiceBusIdentifier identifier, string connectionSettingName = "ServiceBusReplyConnectionString", string entitySettingName = "ServiceBusReplyTopicName")
        {
            registration.ServiceBusReplyIdentifier = identifier;
            registration.ServiceBusReplyConnectionSettingName = connectionSettingName;
            registration.ServiceBusReplyEntitySettingName = entitySettingName;
            return this;
        }

        public Builder WithAppSetting(string key, string value)
        {
            registration.AdditionalSettings[key] = value;
            return this;
        }
    }
}