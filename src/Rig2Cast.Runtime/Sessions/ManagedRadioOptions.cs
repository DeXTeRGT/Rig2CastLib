namespace Rig2Cast.Runtime.Sessions;

public enum ModeApplicabilityPolicy
{
    Enforce,
    Advisory
}

public sealed record ManagedRadioOptions
{
    public ModeApplicabilityPolicy ModeApplicabilityPolicy { get; init; } =
        ModeApplicabilityPolicy.Enforce;

    internal void Validate()
    {
        if (!Enum.IsDefined(ModeApplicabilityPolicy))
            throw new ArgumentOutOfRangeException(nameof(ModeApplicabilityPolicy));
    }
}
