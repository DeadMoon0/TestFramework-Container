namespace TestFramework.Container.Web.Readme;

/// <summary>The row the samples write and read back; stands in for a model of the reader's own.</summary>
public sealed class Order
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Quantity { get; set; }
}
