using TestFramework.Container.Web.Readme.Quickstart;

namespace TestFramework.Container.Web.Readme.Tuning;

internal static class ServerTuning
{
    public static DockerWebEnvironment Environment()
    {
        #region snippet:tuning
        DockerWebEnvironment environment = DockerWebEnvironment.For<SampleSqlDefinition>()
            .Include<ReportingSqlDefinition>()
            .UseSqlImage("mcr.microsoft.com/mssql/server:2019-latest")
            .UseSqlMemoryLimit(2048);
        #endregion
        return environment;
    }
}
