![Icon](https://raw.githubusercontent.com/DeadMoon0/TestFramework-Common/96ef4240c1e55ba95a20b99285219a61407c6355/Assets/Icon.svg)

# TestFramework.Container.Web

Serves the databases a `TestFramework.Web` timeline needs from a Docker container.

A timeline written against a deployed database runs here unchanged. It still names a SQL identifier;
this environment decides that the identifier is served by a container it starts, and publishes the
connection string into the same configuration store a settings file would have filled.

## Install

```bash
dotnet add package TestFramework.Container.Web
```

Targets `net8.0` and `net10.0`. Needs a reachable Docker daemon.

## Quickstart

Declare what the database is made of:

```csharp
internal sealed class SampleSqlDefinition : DockerSqlDefinition
{
    public override SqlIdentifier Identifier => "main";

    protected override void Configure(DockerSqlBuilder builder) => builder
        .WithDatabase("SampleDb")
        .WithSchemaFromModels<Order, Customer>()
        .WithResetMode(SqlResetMode.RecreateDatabase);
}
```

Then point a run at it:

```csharp
ConfigInstance config = ConfigInstance.Create()
    .LoadWebConfig()
    .AddWebSqlModels(models => models.For<Order>().Table("Orders").Key(x => x.Id).MaxLength(x => x.Name, 200))
    .Build();

Timeline timeline = Timeline.Create()
    .SetupArtifact("order")
    .Trigger(WebExt.Sql.Scalar<int>("main", "SELECT COUNT(1) FROM [Orders]")).Name("count")
    .Build();

TimelineRun run = await timeline.SetupRun(config)
    .SetEnv(DockerWebEnvironment.For<SampleSqlDefinition>())
    .AddArtifact("order",
        WebExt.Artifact.Sql.Row<Order>("main", Var.Const("1")),
        new SqlRowArtifactData<Order>(new Order { Id = 1, Name = "sample", Quantity = 3 }))
    .RunAsync();

run.EnsureRanToCompletion();
run.SqlScalar<int>("count").Should().Be(1);
```

`LoadWebConfig()` is required even with no `Sql` section in the settings: it registers the store the
container publishes into.

## Where The Schema Comes From

`WithSchemaFromModels<...>()` derives the tables from the run's own model registry, so the mappings a
test registers with `AddWebSqlModels` shape the generated tables. What a CLR type cannot say —
lengths, precision, identities, nullability — is declared alongside the mapping.

Generation covers schemas, tables, columns, nullability, identities and primary keys. Everything
else a database needs belongs in a script, applied after the generated tables in the order the
scripts are added:

```csharp
builder.WithSchemaFromModels<Order>()
       .WithSchemaScript(SqlScript.FromFile("Schema/views.sql"))
       .WithSchemaScript(SqlScript.FromFile("Schema/reference-data.sql"));
```

A generated schema is scaffolding for a database the test owns, not a migration tool. Where the real
schema is owned elsewhere — by migrations, or by whoever runs the server — use a script that mirrors
it, because a table generated from test-side models proves only that the models agree with
themselves.

## Reset Modes

A container can outlive a single run, so what a previous run left behind is a real concern.

| Mode | Behaviour |
|---|---|
| `None` | create the database when missing, keep whatever it contains |
| `RunResetScript` | run `WithResetScript(...)` once the schema is in place |
| `RecreateDatabase` | drop and create, so every run starts from the declared schema alone |

Provisioning order is fixed: the database exists, then the generated tables, then the declared
scripts, then the reset script. A reset that ran first would fail on the first run, when there is
nothing yet to clear.

## Two Addresses, Again

The component publishes the **host** connection string to the test process, and keeps the **network**
one for the containers that will later be given the database:

```csharp
SqlServerComponentState state = run.EnvironmentContext.GetState<SqlServerComponentState>(DockerWebEnvironment.SqlServerComponentId);
SqlDatabaseEndpoint endpoint = state.GetRequiredDatabase("main");
// endpoint.HostConnectionString    -> localhost,<mapped port>
// endpoint.NetworkConnectionString -> sqlserver,1433
```

## Tuning The Server

One container serves every declared database, so the server settings belong to the environment
rather than to a definition:

```csharp
DockerWebEnvironment.For<SampleSqlDefinition>()
    .Include<ReportingSqlDefinition>()
    .UseSqlImage("mcr.microsoft.com/mssql/server:2019-latest")
    .UseSqlMemoryLimit(2048)
```

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `The run has no SQL configuration store` | `LoadWebConfig()` was not called on the config the run uses. |
| `which no included definition declares` | A step or artifact names an identifier no `DockerSqlDefinition` was included for. The message lists what is declared. |
| `does not name a database` | The definition's `Configure` never called `WithDatabase(...)`. |
| `is not a plain identifier` | A database name goes into a statement verbatim, so only letters, digits and underscores are accepted. |
| `the SQL Server container did not become usable` | The engine did not start within the readiness window. Check `docker logs`; a low memory limit is the usual cause. |
| Rows survive between runs | The default reset mode keeps them. Choose `RecreateDatabase`, or supply a reset script. |

## Scope

This package provides the SQL Server side of a container-backed web environment. Hosting the
application under test in a container is not part of it yet.
