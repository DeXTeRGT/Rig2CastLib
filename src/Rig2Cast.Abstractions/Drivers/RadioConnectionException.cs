namespace Rig2Cast.Abstractions.Drivers;

/// <summary>
/// Identifies a transport or protocol-session failure that makes the current
/// physical radio connection unsafe to reuse.
/// </summary>
public sealed class RadioConnectionException : IOException
{
    public RadioConnectionException(string message) : base(message)
    {
    }

    public RadioConnectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
