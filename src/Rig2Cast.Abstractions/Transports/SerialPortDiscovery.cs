namespace Rig2Cast.Abstractions.Transports;

public sealed record SerialPortDescriptor(string PortName, string DisplayName);

/// <summary>Discovers serial ports without exposing an operating-system-specific API to hosts.</summary>
public interface ISerialPortDiscovery
{
    IReadOnlyList<SerialPortDescriptor> GetPorts();
}
