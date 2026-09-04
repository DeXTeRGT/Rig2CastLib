using System.IO.Ports;
using Rig2Cast.Abstractions.Transports;

namespace Rig2Cast.Transports.Serial;

/// <summary>Cross-platform serial-port discovery backed by System.IO.Ports.</summary>
public sealed class SystemSerialPortDiscovery : ISerialPortDiscovery
{
    private readonly Func<string[]> _getPortNames;

    public SystemSerialPortDiscovery() : this(SerialPort.GetPortNames)
    {
    }

    public SystemSerialPortDiscovery(Func<string[]> getPortNames)
    {
        ArgumentNullException.ThrowIfNull(getPortNames);
        _getPortNames = getPortNames;
    }

    public IReadOnlyList<SerialPortDescriptor> GetPorts() => _getPortNames()
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(NaturalSortKey, StringComparer.OrdinalIgnoreCase)
        .Select(name => new SerialPortDescriptor(name, name))
        .ToArray();

    private static string NaturalSortKey(string name)
    {
        int prefixLength = name.TakeWhile(character => !char.IsDigit(character)).Count();
        return prefixLength < name.Length && long.TryParse(name.AsSpan(prefixLength), out long suffix)
            ? $"{name[..prefixLength]}{suffix:D20}"
            : name;
    }
}
