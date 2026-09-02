namespace Rig2Cast.PluginHost;

public sealed class PluginLoadException(
    PluginLoadStatus status,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public PluginLoadStatus Status { get; } = status;
}
