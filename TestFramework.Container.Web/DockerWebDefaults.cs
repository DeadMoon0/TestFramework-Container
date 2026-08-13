using System;

namespace TestFramework.Container.Web;

/// <summary>
/// Defaults every container-backed web environment starts from.
/// </summary>
public static class DockerWebDefaults
{
    /// <summary>
    /// The SQL Server image started for the environment.
    /// </summary>
    public const string MsSqlImage = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";

    /// <summary>
    /// The memory limit handed to the SQL Server engine.
    /// </summary>
    public const int MsSqlMemoryLimitMb = 1536;

    /// <summary>
    /// The <c>sa</c> password of the started server.
    /// </summary>
    /// <remarks>
    /// This is a throwaway credential for a container that exists for the length of a test run. It
    /// is a constant so that a reused container is reachable across runs.
    /// </remarks>
    public const string MsSqlPassword = "TestFramework_Container1!";

    /// <summary>
    /// The alias other containers on the same network use to reach SQL Server.
    /// </summary>
    public const string MsSqlNetworkAlias = "sqlserver";

    /// <summary>
    /// The prefix of the Docker network the environment creates.
    /// </summary>
    public const string NetworkNamePrefix = "testframework-web";

    /// <summary>
    /// How long to wait for a started SQL Server to answer.
    /// </summary>
    public static readonly TimeSpan MsSqlReadinessTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// The repository the ASP.NET runtime image is taken from.
    /// </summary>
    public const string AspNetImageRepository = "mcr.microsoft.com/dotnet/aspnet";

    /// <summary>
    /// The directory an application's build output is placed in inside its container.
    /// </summary>
    public const string ApiRoot = "/app";

    /// <summary>
    /// The hosting environment name an application runs under unless it declares another.
    /// </summary>
    public const string ApiEnvironmentName = "Testing";

    /// <summary>
    /// The path probed until an application answers, unless it declares another.
    /// </summary>
    public const string ApiHealthPath = "/health";

    /// <summary>
    /// The port an application listens on inside its container.
    /// </summary>
    public const int ApiInternalPort = 8080;

    /// <summary>
    /// How long to wait for a started application to answer.
    /// </summary>
    public static readonly TimeSpan ApiReadinessTimeout = TimeSpan.FromMinutes(2);
}
