namespace Rig2Cast.Drivers.Elecraft.Protocol;

public sealed class ElecraftCommandRejectedException(string command) : InvalidOperationException(
    $"The Elecraft radio rejected CAT command '{command}' with '?;'.")
{
    public string Command { get; } = command;
}
