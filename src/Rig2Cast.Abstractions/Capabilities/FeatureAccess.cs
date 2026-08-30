namespace Rig2Cast.Abstractions.Capabilities;

[Flags]
public enum FeatureAccess
{
    None = 0,
    Read = 1,
    Write = 2
}
