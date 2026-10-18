using TestFramework.Core.Timelines;

namespace TestFramework.Container.Web.Readme.Addresses;

internal static class TwoAddresses
{
    public static SqlDatabaseEndpoint Endpoint(TimelineRun run)
    {
        #region snippet:two-addresses
        SqlServerComponentState state = run.EnvironmentContext.GetState<SqlServerComponentState>(DockerWebEnvironment.SqlServerComponentId)!;
        SqlDatabaseEndpoint endpoint = state.GetRequiredDatabase("main");
        // endpoint.HostConnectionString    -> localhost,<mapped port>   (published to the timeline)
        // endpoint.NetworkConnectionString -> sqlserver,1433            (injected via UseSql)
        #endregion
        return endpoint;
    }
}
