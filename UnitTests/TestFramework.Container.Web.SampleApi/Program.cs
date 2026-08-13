using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using TestFramework.Container.Web.SampleApi;

// A deliberately small application under test: it reads its connection string from configuration and
// talks to SQL Server with plain ADO, so nothing here depends on the test framework or on an ORM.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
WebApplication app = builder.Build();

// Resolved per request rather than at startup, so the health endpoint still answers for an
// application that was started without a database.
string ConnectionString() => app.Configuration.GetConnectionString("Sales")
    ?? throw new InvalidOperationException("The connection string 'Sales' is not configured.");

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapGet("/api/orders", async () =>
{
    List<SampleOrder> orders = [];

    await using SqlConnection connection = new(ConnectionString());
    await connection.OpenAsync().ConfigureAwait(false);
    await using SqlCommand command = new("SELECT [Id], [Name], [Quantity] FROM [Orders] ORDER BY [Id];", connection);
    await using SqlDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

    while (await reader.ReadAsync().ConfigureAwait(false))
        orders.Add(new SampleOrder(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2)));

    return Results.Ok(orders);
});

app.MapPost("/api/orders", async (CreateSampleOrder order) =>
{
    if (string.IsNullOrWhiteSpace(order.Name))
        return Results.BadRequest(new { error = "Name is required." });

    await using SqlConnection connection = new(ConnectionString());
    await connection.OpenAsync().ConfigureAwait(false);
    await using SqlCommand command = new(
        "INSERT INTO [Orders] ([Name], [Quantity]) VALUES (@name, @quantity); SELECT CAST(SCOPE_IDENTITY() AS INT);",
        connection);

    command.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = order.Name;
    command.Parameters.Add("@quantity", SqlDbType.Int).Value = order.Quantity;

    object? assignedId = await command.ExecuteScalarAsync().ConfigureAwait(false);
    if (assignedId is not int id)
        return Results.Problem("The database did not assign an identity.");

    return Results.Created(
        $"/api/orders/{id.ToString(CultureInfo.InvariantCulture)}",
        new SampleOrder(id, order.Name, order.Quantity));
});

await app.RunAsync().ConfigureAwait(false);
