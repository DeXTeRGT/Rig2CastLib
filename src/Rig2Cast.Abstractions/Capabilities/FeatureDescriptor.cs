namespace Rig2Cast.Abstractions.Capabilities;

public sealed record FeatureDescriptor(
    CapabilitySupport Support,
    FeatureAccess Access,
    string? RequiredLease = null,
    string? Detail = null);
