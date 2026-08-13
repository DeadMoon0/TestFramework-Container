namespace TestFramework.Container.Web.SampleApi;

/// <summary>
/// An order as the API returns it.
/// </summary>
/// <param name="Id">The database-assigned key.</param>
/// <param name="Name">The order name.</param>
/// <param name="Quantity">The ordered quantity.</param>
public sealed record SampleOrder(int Id, string Name, int Quantity);

/// <summary>
/// An order as a caller creates it, without the key the database assigns.
/// </summary>
/// <param name="Name">The order name.</param>
/// <param name="Quantity">The ordered quantity.</param>
public sealed record CreateSampleOrder(string Name, int Quantity);
