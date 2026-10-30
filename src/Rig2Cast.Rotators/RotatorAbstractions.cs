namespace Rig2Cast.Rotators;

public enum RotatorAxis { Azimuth, AzimuthElevation }
public enum RotatorDirection { Clockwise, Counterclockwise }

public sealed record RotatorCapabilities(
    RotatorAxis Axis,
    double MinimumAzimuth,
    double MaximumAzimuth,
    bool CanRead,
    bool CanMoveTo,
    bool CanMoveManually,
    bool CanStop,
    bool CanPark);

public sealed record RotatorPosition(double Azimuth, double? Elevation = null);

public sealed record RotatorModelDescriptor(
    string Id,
    string Manufacturer,
    string Model,
    IReadOnlyList<string> SupportedTransports,
    int DefaultBaudRate);

public interface IRotatorTransport : IAsyncDisposable
{
    ValueTask OpenAsync(CancellationToken cancellationToken);
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}

public interface IRotatorDriver : IAsyncDisposable
{
    RotatorCapabilities Capabilities { get; }
    ValueTask<RotatorPosition> ReadPositionAsync(CancellationToken cancellationToken);
    ValueTask MoveToAsync(RotatorPosition position, CancellationToken cancellationToken);
    ValueTask StartAsync(RotatorDirection direction, CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
    ValueTask ParkAsync(CancellationToken cancellationToken);
}

public interface IRotatorDriverFactory
{
    RotatorModelDescriptor Model { get; }
    ValueTask<IRotatorDriver> OpenAsync(
        IRotatorTransport transport,
        IReadOnlyDictionary<string, string> settings,
        CancellationToken cancellationToken);
}

public sealed class RotatorDriverCatalog
{
    private readonly Dictionary<string, IRotatorDriverFactory> _factories = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<RotatorModelDescriptor> Models => _factories.Values.Select(factory => factory.Model).ToArray();
    public void Register(IRotatorDriverFactory factory) => _factories.Add(factory.Model.Id, factory);
    public IRotatorDriverFactory Find(string modelId) => _factories.TryGetValue(modelId, out IRotatorDriverFactory? factory)
        ? factory : throw new KeyNotFoundException($"Rotator model '{modelId}' is not registered.");
}
