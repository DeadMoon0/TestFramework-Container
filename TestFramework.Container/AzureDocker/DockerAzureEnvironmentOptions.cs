using TestFramework.Azure.Identifier;
using TestFramework.Core.Environment;

namespace TestFramework.Container.AzureDocker;

public class DockerAzureEnvironmentOptions
{
    public string MsSqlImage { get; init; } = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";
    public string AzuriteImage { get; init; } = "mcr.microsoft.com/azure-storage/azurite:3.33.0";
    public string CosmosDbImage { get; init; } = "mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview";
    public string ServiceBusImage { get; init; } = "mcr.microsoft.com/azure-messaging/servicebus-emulator:latest";
    public string MsSqlPassword { get; init; } = "TestFramework_Container1!";
    public string ServiceBusTopologyConfigPath { get; init; } = Path.Combine("AzureDocker", "Configurations", "ServiceBus", "config.json");
    public IReadOnlyCollection<EnvComponentIdentifier> RequiredComponents { get; init; } = [];
    public IReadOnlyCollection<ServiceBusIdentifier> RequiredServiceBusIdentifiers { get; init; } = [];
    public IReadOnlyCollection<StorageAccountIdentifier> RequiredStorageIdentifiers { get; init; } = [];
    public IReadOnlyCollection<CosmosContainerIdentifier> RequiredCosmosIdentifiers { get; init; } = [];
    public IReadOnlyCollection<SqlDatabaseIdentifier> RequiredSqlIdentifiers { get; init; } = [];
}