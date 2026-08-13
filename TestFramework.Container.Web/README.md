![Icon](https://raw.githubusercontent.com/DeadMoon0/TestFramework-Common/96ef4240c1e55ba95a20b99285219a61407c6355/Assets/Icon.svg)

# TestFramework.Container.Web

Serves the API and the database a `TestFramework.Web` timeline needs from Docker containers.

A timeline written against a deployed system runs here unchanged. It still names an identifier; this
environment decides that the identifier is served by a container it starts, and publishes the address
into the same configuration store a settings file would have filled.

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

## Running The Application Too

Declare the application the same way, pointing a configuration value at the database:

```csharp
// in the application project — the generated Program of a minimal-hosting app is internal
public sealed class OrdersApiMarker;

// in the test project
internal sealed class OrdersApiDefinition : DockerApiDefinition<OrdersApiMarker>
{
    public override ApiIdentifier Identifier => "orders";

    protected override void Configure(DockerApiBuilder builder) => builder
        .WithEnvironmentName("Testing")
        .WithHealthPath("/health")
        .UseSql<SalesSqlDefinition>("ConnectionStrings:Sales")
        .WithSetting("Features:UseFakeClock", "true");
}
```

Both go into the same environment, and the timeline calls the API while asserting on the database
behind it:

```csharp
Timeline timeline = Timeline.Create()
    .Trigger(WebExt.Api.Http("orders").Post("api/orders")
        .WithJsonBody(Var.Const(new CreateOrder("container-order", 7))).Call()).Name("create")
    .FindArtifact("created", WebExt.ArtifactFinder.Sql.Where<Order>("sales", "Name = @name")
        .WithParameter("name", Var.Const("container-order")))
    .Build();

TimelineRun run = await timeline.SetupRun(config)
    .SetEnv(DockerWebEnvironment.For<SalesSqlDefinition>().Include<OrdersApiDefinition>())
    .RunAsync();

run.EnsureRanToCompletion();
run.ApiStatus("create").Should().Be(HttpStatusCode.Created);
run.SqlRow<Order>("created").Select(order => order.Quantity).Should().Be(7);
```

The response says what the API claims; the row says what actually happened.

### How the application gets there

The application's **own** build output is copied into an ASP.NET runtime image chosen from the
framework it was built for — `net10.0` becomes `mcr.microsoft.com/dotnet/aspnet:10.0`. No publish
step, so a rerun costs a build.

Because resolution is a convenience and not obvious, what was shipped is stated in the run log and
kept on the component state. When you want no guessing at all, say it outright:

```csharp
builder.WithOutputDirectory(@"C:\src\Orders.Api\bin\Release\net10.0")
       .WithImage("my-registry/orders-api:local");
```

### How configuration gets there

Settings become a generated `appsettings.<Environment>.json` copied in beside the application, not a
set of doubly underscored environment variables — a file can be read back when a run misbehaves. It
is written to the run log, and kept on the state:

```csharp
ApiComponentState state = run.EnvironmentContext.GetState<ApiComponentState>(DockerWebEnvironment.ApiComponentId);
RunningApi api = state.GetRequiredApi("orders");
// api.SettingsJson, api.ShippedDirectory, api.BaseUrl
```

`WithEnvironmentVariable(...)` remains for the values that really are environment variables.

### Applications are never reused

A database is worth keeping warm across runs; an application is not. A reused container would go on
serving the code it started with, so an edit-and-rerun cycle would silently test the previous build.
Databases have reset modes for stale *data*; there is no equivalent for a stale binary, so the
application container is per-run and the SQL container is not.

## Two Addresses, Again

The test process gets the **host** connection string; the application container gets the **network**
one. Both describe the same database, and handing over the wrong one is the classic failure:

```csharp
SqlServerComponentState state = run.EnvironmentContext.GetState<SqlServerComponentState>(DockerWebEnvironment.SqlServerComponentId);
SqlDatabaseEndpoint endpoint = state.GetRequiredDatabase("main");
// endpoint.HostConnectionString    -> localhost,<mapped port>   (published to the timeline)
// endpoint.NetworkConnectionString -> sqlserver,1433            (injected via UseSql)
```

`UseSql<T>(...)` always injects the network form, so this is one thing a test author cannot get wrong.

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
| `The run has no API configuration store` | The same, for an application. |
| `which no included definition declares` | A step, artifact or binding names an identifier no definition was included for. The message lists what is declared. |
| `does not name a database` | The definition's `Configure` never called `WithDatabase(...)`. |
| `is not a plain identifier` | A database name goes into a statement verbatim, so only letters, digits and underscores are accepted. |
| `the SQL Server container did not become usable` | The engine did not start within the readiness window. Check `docker logs`; a low memory limit is the usual cause. |
| `did not answer within ...` for an API | The application failed to start. Its own log is already captured into the run output — read that before anything else. |
| `both directly and from a database binding` | A setting is written by `WithSetting` and by `UseSql` at once, so which wins is not obvious. Remove one. |
| Rows survive between runs | The default reset mode keeps them. Choose `RecreateDatabase`, or supply a reset script. |
| A code change seems to have no effect | The application container is per-run, so this should not happen — but check that the test project really references the application project, since that is what builds the shipped output. |

## Scope

This package runs the application under test and the SQL Server behind it. Stubbing the services
*that* application calls is not part of it yet.
