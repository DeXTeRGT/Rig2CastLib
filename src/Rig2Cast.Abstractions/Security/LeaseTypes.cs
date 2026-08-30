namespace Rig2Cast.Abstractions.Security;

public static class LeaseKinds
{
    public const string Transmit = "radio.transmit";
    public const string ExclusiveControl = "radio.exclusive-control";
}

public sealed record LeaseToken(
    Guid Value,
    string Kind,
    ClientIdentity Owner,
    DateTimeOffset ExpiresAt);

public sealed record LeaseSnapshot(
    long Revision,
    IReadOnlyList<LeaseToken> Active);
