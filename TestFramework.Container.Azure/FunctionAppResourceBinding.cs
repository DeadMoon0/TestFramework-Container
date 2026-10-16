namespace TestFramework.Container.Azure;

internal sealed record FunctionAppResourceBinding(
    FunctionAppResourceBindingKind Kind,
    string ResourceIdentifier,
    string PrimarySettingName,
    string? SecondarySettingName = null,
    string? TertiarySettingName = null,
    DockerServiceBusEndpoint? ServiceBusEndpoint = null);
