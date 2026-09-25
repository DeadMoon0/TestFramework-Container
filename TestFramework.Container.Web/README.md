![Icon](https://raw.githubusercontent.com/DeadMoon0/TestFramework-Common/96ef4240c1e55ba95a20b99285219a61407c6355/Assets/Icon.svg)

# TestFramework.Container.Web

Serves the API, the database and the stubbed dependencies a `TestFramework.Web` timeline needs from Docker containers.

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
internal sealed class OrdersApiDefinition : DockerApiDefinition
{
    public override ApiIdentifier Identifier => "orders";

    // Named, not discovered. A relative path resolves against this source file.
    public override ContainerSource Source =>
        ContainerSource.Project("../Orders.Api/Orders.Api.csproj").WithTargetFramework("net10.0");

    protected override void Configure(DockerApiBuilder builder) => builder
        .WithEnvironmentName("Testing")
        .WithHealthPath("/health")
        .UseSql<SalesSqlDefinition>("ConnectionStrings:Sales")
        .WithSetting("Features:UseFakeClock", "true");
}
```

The test project does **not** reference the application project. That is the point: the application
stays a black box, addressed only by the paths it exposes.

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

By default the SDK builds an image from the project — no Dockerfile, and the base image follows the
project's framework, so `net10.0` becomes `mcr.microsoft.com/dotnet/aspnet:10.0`. Two other
strategies are one call away:

```csharp
ContainerSource.Project(path)                     // SDK builds the image        ~8 s
ContainerSource.Project(path).BuiltOnHost()       // publish to temp, copy in    ~2 s
ContainerSource.Project(path).BuiltInContainer()  // built in Docker, clean host ~6 s
```

`BuiltInContainer` leaves no build output on the host at all and needs **no feed credentials**: the
host restores with the configuration that already works, and the packages that produced are handed
to the container as a pre-populated cache with every NuGet source cleared. It requires the target
framework generation to match the SDK on the machine, and says so while planning rather than failing
inside the container.

Other sources are available where a project is not the right answer:

```csharp
ContainerSource.Image("orders-api:ci-1234")            // a pipeline already built it
ContainerSource.Directory(@"C:\out\orders-api")        // this exact folder
```

Whatever the source, the plan is written to the run log before anything starts and kept on the
component state, so what actually ran is never something you have to infer.

### How configuration gets there

Settings become a generated `appsettings.<Environment>.json` copied in beside the application, not a
set of doubly underscored environment variables — a file can be read back when a run misbehaves. It
is written to the run log, and kept on the state:

```csharp
ApiComponentState state = run.EnvironmentContext.GetState<ApiComponentState>(DockerWebEnvironment.ApiComponentId);
RunningApi api = state.GetRequiredApi("orders");
// api.SettingsJson, api.BaseUrl, api.Plan.ProjectPath, api.Plan.Image, api.Plan.BuiltAtUtc
```

`WithEnvironmentVariable(...)` remains for the values that really are environment variables.

### Applications are never reused

A database is worth keeping warm across runs; an application is not. A reused container would go on
serving the code it started with, so an edit-and-rerun cycle would silently test the previous build.
Databases have reset modes for stale *data*; there is no equivalent for a stale binary, so the
application container is per-run and the SQL container is not.

## Stubbing What The Application Calls

A stub definition comes from `TestFramework.Web` and says nothing about hosting — declaring it here
is what decides that a container serves it:

```csharp
internal sealed class PaymentsStubDefinition : StubDefinition
{
    public override StubIdentifier Identifier => "payments";

    protected override void Configure(StubMappingBuilder builder) => builder
        .OnGet("/api/rates/EUR")
            .RespondJson(HttpStatusCode.OK, new { currency = "EUR", rate = 1.08 })
        .OnPost("/api/charges")
            .WithHeader("Idempotency-Key")
            .RespondJson(HttpStatusCode.Created, new { id = "{{Random Type=Guid}}" }, useTemplating: true);
}
```

The application is pointed at it the same way it is pointed at a database:

```csharp
protected override void Configure(DockerApiBuilder builder) => builder
    .UseSql<SalesSqlDefinition>("ConnectionStrings:Sales")
    .UseStub<PaymentsStubDefinition>("Services:Payments:BaseUrl");

DockerWebEnvironment.For<SalesSqlDefinition>()
    .Include<OrdersApiDefinition>()
    .IncludeStub<PaymentsStubDefinition>();
```

Then the timeline asserts on what the application sent **outwards**, which no response body can show:

```csharp
.WaitForEvent(WebExt.Stub.Called("payments", HttpMethod.Post, "/api/charges")).Name("charged")
.Trigger(WebExt.Stub.Calls("payments")).Name("calls")

run.StubCall("charged").Select(call => call.Body).Should().Contain("\"amount\":30");
run.StubCalls("calls").Should().HaveCount(1);
run.StubUnmatchedCalls("calls").Should().HaveCount(0);   // nothing was called that was not declared
```

Mappings are declarative because a container cannot call back into the test process. Handlebars
templating (`{{request.body.amount}}`) covers the cases a C# callback would otherwise be reached for.

Two things worth knowing:

- **Mappings are verified on startup.** A mapping the server rejects is simply absent, and every call
  to it would answer `404` for no visible reason, so the component compares declared against loaded
  and fails immediately with the container log if they differ.
- **The image follows `latest`.** Its publisher does not tag releases; pin it with
  `UseStubImage(...)` when a run has to be reproducible over time.

## Serving The Frontend Too

A site is the application a browser loads: an Angular build, any other npm framework's output, or a
plain folder of pages. Nothing framework-specific is baked into the container kind — a fixed nginx
image gets the payload copied in, and the `Source` only says where that payload comes from:

```csharp
internal sealed class ShopSiteDefinition : DockerSiteDefinition
{
    public override SiteIdentifier Identifier => "shop";   // the same string browser steps use

    public override SiteSource Source =>
        SiteSource.NpmProject("../../Shop.Frontend");       // npm run build, dist probed by convention

    protected override void Configure(DockerSiteBuilder builder) => builder
        .ProxyApi<OrdersApiDefinition>("/api");
}

DockerWebEnvironment.For<SalesSqlDefinition>()
    .Include<OrdersApiDefinition>()
    .Include<ShopSiteDefinition>();
```

The site publishes its host address into the `Site` configuration store, so a browser timeline
resolves it by identifier like every other resource — and a deployed site is the same identifier
with a `Site:shop:BaseUrl` entry instead of a container.

### Where the payload comes from

| Source | Build environment needed |
|---|---|
| `SiteSource.Directory(@"..\dist\shop\browser")` | none — an already-built folder |
| `SiteSource.NpmProject("../Shop.Frontend")` | node + npm on the host; `ng`/`vite` come from `node_modules` |
| `...NpmProject(...).BuiltInContainer()` | only Docker — sources are copied into a node image, `npm ci` and the build run there, the output ships |
| `SiteSource.ContainerBuild("../docs", "hugomods/hugo", ["hugo"]).WithDistPath("public")` | the named build image; any toolchain |
| `SiteSource.Image("shop-ui:ci-1234")` | none — runs as-is, with whatever server it bakes in |

The plan is stated and logged before anything happens, exactly like an application's. An npm project
without a declared `WithDistPath(...)` is probed by convention (`dist/*/browser` first, the Angular
layout) — and exactly one answer is accepted; two plausible output folders are an error, not a guess.

### How the browser reaches the API

The SPA either calls a **relative** path, or reads an **absolute** address from its runtime
configuration. Both are declared on the definition, and they write different addresses on purpose:

| App style | Declaration | Address written | CORS |
|---|---|---|---|
| calls `/api` relative | `.ProxyApi<OrdersApiDefinition>("/api")` | network URL, inside the generated nginx config | none needed — same origin, like production |
| reads `apiBaseUrl` from config | `.ConfigJsonApi<OrdersApiDefinition>("apiBaseUrl")` | host URL, in the generated `assets/config.json` | the application must allow it |

The proxied road works because the browser resolves a relative path against the page's own origin —
the site container — whose nginx forwards it over the Docker network. The browser never needs a
route to the API container at all.

A target may also be named by identifier instead of type: `.ProxyApi("orders", "/api")` resolves to
the declared container when the environment runs one, and to the `Api:orders:BaseUrl` configuration
entry when the backend is deployed elsewhere. Same store either way, so moving the API between the
two is one `Include<>()`.

### Runtime configuration of any shape

The declarative bindings write into one JSON file (default `assets/config.json`), **merging over**
the file the payload ships: a checked-in `{ "apiBaseUrl": null, "theme": "dark" }` keeps `theme`.
For every other shape — `env.js`, `window.__env`, a patched page — one composer covers it:

```csharp
.WithConfigFile("env.js", ctx =>
    $"window.__env = {{ ordersApi: \"{ctx.Addresses.ApiBaseUrl("orders")}\" }};")

// or patch one field of the shipped JSON without rebuilding the rest:
.WithConfigFile("assets/config.json", ctx =>
    ctx.MergeJson(("apiBaseUrl", ctx.Addresses.ApiBaseUrl("orders").ToString())))
```

Every generated file — the nginx config included — is logged verbatim and kept on the component
state, so a test can state what was actually served:

```csharp
SiteComponentState state = run.EnvironmentContext.GetState<SiteComponentState>(DockerWebEnvironment.SiteComponentId);
RunningSite site = state.GetRequiredSite("shop");
// site.BaseUrl, site.NginxConfig, site.GeneratedFiles["assets/config.json"], site.Plan
```

### What the server does for a SPA

Deep links fall back to `index.html` by default (`WithoutSpaFallback()` turns that off for a
classic multi-page folder), `location` blocks carry generated comments naming the `proxy_pass`
trailing-slash rule they encode, and `WithNginxServerDirective(...)` appends anything no declaration
covers, verbatim. An `Image` source runs as-is: declaring proxy routes or fallback changes against
it fails fast, because its server configuration is baked in.

Sites are per-run for the same reason applications are: a reused container would serve the previous
build and proxy to containers that are gone. Linux containers are required, as they already are for
SQL Server and the stub server.

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
| `declared N mapping(s) but the server loaded M` | The stub server rejected a mapping. Its log, already captured, names the file. |
| `requires ... and nothing in this run declares it` | A step names an identifier that neither an included definition nor configuration declares. The engine refuses it before anything starts and lists what is declared. An identifier configuration declares is left to that system: one run can mix containers with deployed ones. |
| `which no included definition declares` | A site or API binding names an identifier no definition was included for. The message lists what is declared. |
| `does not name a database` | The definition's `Configure` never called `WithDatabase(...)`. |
| `is not a plain identifier` | A database name goes into a statement verbatim, so only letters, digits and underscores are accepted. |
| `the SQL Server container did not become usable` | The engine did not start within the readiness window. Check `docker logs`; a low memory limit is the usual cause. |
| `did not answer within ...` for an API | The application failed to start. Its own log is already captured into the run output — read that before anything else. |
| `both directly and from a resource binding` | A setting is written by `WithSetting` and by `UseSql`/`UseStub` at once, so which wins is not obvious. Remove one. |
| `targets ..., so which one to run is ambiguous` | A multi-targeted project needs `WithTargetFramework(...)`; picking silently would let a project change what a test runs. |
| `No 'docker' executable was found` | The SDK shells out to the CLI to build an image. Install it, or use `BuiltOnHost()`, which needs none. |
| `The Docker daemon is serving 'windows' containers` | The .NET base images used here are Linux images. Switch Docker Desktop, or name a matching image. |
| `needs a .NET N SDK, and this machine restores with M` | An offline in-container build must target the SDK generation that resolved the packages. Target that framework, or use `BuiltAsImage()`. |
| `The container build for '...' failed` | The build output follows the message and the context is kept at the named path for inspection. |
| Rows survive between runs | The default reset mode keeps them. Choose `RecreateDatabase`, or supply a reset script. |
| A code change seems to have no effect | The application container is per-run and the framework builds the project itself, so this should not happen. Check the plan in the run log for which project was built. |

## Scope

This package runs the application under test, the SQL Server behind it, and the stubbed dependencies
in front of it. It does not host an application in the test process; that is deliberately a different
environment, not a mode of this one.
