namespace TestFramework.Container.Sources;

/// <summary>
/// An image this process built, and the sources it was built from.
/// </summary>
/// <param name="Plan">The plan the build produced.</param>
/// <param name="Fingerprint">When its sources last changed, as <see cref="SourceFingerprint"/> read it.</param>
internal sealed record BuiltImage(ContainerSourcePlan Plan, string Fingerprint);
