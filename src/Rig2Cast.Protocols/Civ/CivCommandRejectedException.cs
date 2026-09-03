namespace Rig2Cast.Protocols.Civ;

public sealed class CivCommandRejectedException() : InvalidOperationException(
    "The radio rejected the CI-V command.");
