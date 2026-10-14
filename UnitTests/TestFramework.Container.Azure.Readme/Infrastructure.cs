using System.Threading.Tasks;
using TestFramework.Config;
using TestFramework.Container.Azure.Readme.Composition;
using TestFramework.Core.Timelines;

namespace TestFramework.Container.Azure.Readme.Infrastructure;

#region snippet:infrastructure
public sealed class CustomInfrastructure : DockerAzureInfrastructureDefinition
{
    public override string? AzuriteImage => "mcr.microsoft.com/azure-storage/azurite:3.35.0";
    public override string? CosmosDbImage => "mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview";
    public override string? MsSqlImage => "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";
    public override string? ServiceBusImage => "mcr.microsoft.com/azure-messaging/servicebus-emulator:latest";
    public override string? MsSqlPassword => "Your_Own_Password1!";

    protected override void ConfigureServiceBusTopology(DockerServiceBusTopologyBuilder builder)
    {
        builder.AddNamespace("sbemulatorns", ns => ns
            .AddQueue("processing-input")
            .AddTopic("processing-reply", topic => topic.AddSubscription("Default")));
    }
}
#endregion

internal static class InfrastructureRun
{
    public static async Task<TimelineRun> Run(Timeline timeline, ConfigInstance config)
    {
        #region snippet:infrastructure
        TimelineRun run = await timeline
            .SetupRun(config)
            .SetEnv(DockerAzureEnvironment.For<DefaultFunctionApp>().Include<CustomInfrastructure>())
            .RunAsync();
        #endregion
        return run;
    }
}
