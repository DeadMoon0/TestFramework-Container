using TestFramework.Web.Sql;

namespace TestFramework.Container.Web.Readme;

/// <summary>A second database on the same server, for the tuning sample.</summary>
public sealed class ReportingSqlDefinition : DockerSqlDefinition
{
    public override SqlIdentifier Identifier => "reporting";

    protected override void Configure(DockerSqlBuilder builder) => builder.WithDatabase("ReportingDb");
}
