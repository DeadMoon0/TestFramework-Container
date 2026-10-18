namespace TestFramework.Container.Web.Readme;

/// <summary>The request body the samples post; stands in for the application's own contract.</summary>
public sealed record CreateOrder(string Name, int Quantity);
