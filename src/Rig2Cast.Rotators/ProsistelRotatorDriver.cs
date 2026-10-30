using System.Globalization;
using System.Text;

namespace Rig2Cast.Rotators;

public sealed class ProsistelRotatorDriverFactory : IRotatorDriverFactory
{
    public RotatorModelDescriptor Model { get; } = new(
        "prosistel.pst", "Prosistel", "PST compatible", ["Tcp", "Serial"], 9600);

    public async ValueTask<IRotatorDriver> OpenAsync(IRotatorTransport transport,
        IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken)
    {
        await transport.OpenAsync(cancellationToken).ConfigureAwait(false);
        double maximum = settings.TryGetValue("maximumAzimuth", out string? configured) &&
                         double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value : 360;
        var driver = new ProsistelRotatorDriver(transport, maximum);
        try
        {
            await driver.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return driver;
        }
        catch
        {
            await driver.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

public sealed class ProsistelRotatorDriver : IRotatorDriver
{
    private const byte Stx = 0x02;
    private const byte Cr = 0x0d;
    private static readonly Encoding Ascii = Encoding.ASCII;
    private readonly IRotatorTransport _transport;
    private readonly byte[] _oneByte = new byte[1];

    public ProsistelRotatorDriver(IRotatorTransport transport, double maximumAzimuth = 360)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumAzimuth);
        _transport = transport;
        Capabilities = new RotatorCapabilities(
            RotatorAxis.Azimuth, 0, maximumAzimuth, true, true, false, true, false);
    }

    public RotatorCapabilities Capabilities { get; }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        await SendAsync("AS", cancellationToken).ConfigureAwait(false);
        await ReadAcknowledgementAsync("S", cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<RotatorPosition> ReadPositionAsync(CancellationToken cancellationToken)
    {
        await SendAsync("A?", cancellationToken).ConfigureAwait(false);
        while (true)
        {
            string[] fields = Split(await ReadFrameAsync(cancellationToken).ConfigureAwait(false));
            if (IsError(fields, "?")) ThrowControllerError(fields);
            if (fields is ["A", "?", string position, "B" or "R"] &&
                double.TryParse(position, NumberStyles.Float, CultureInfo.InvariantCulture, out double azimuth))
                return new RotatorPosition(azimuth);
        }
    }

    public async ValueTask MoveToAsync(RotatorPosition position, CancellationToken cancellationToken)
    {
        if (position.Azimuth < Capabilities.MinimumAzimuth || position.Azimuth > Capabilities.MaximumAzimuth)
            throw new ArgumentOutOfRangeException(nameof(position));
        long target = checked((long)Math.Round(position.Azimuth, MidpointRounding.AwayFromZero));
        await SendAsync($"AG{target.ToString(CultureInfo.InvariantCulture)}", cancellationToken).ConfigureAwait(false);
        await ReadAcknowledgementAsync("G", cancellationToken).ConfigureAwait(false);
    }

    public ValueTask StartAsync(RotatorDirection direction, CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException("The verified Prosistel profile does not support manual rotation commands."));

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await SendAsync("AG977", cancellationToken).ConfigureAwait(false);
        await ReadAcknowledgementAsync("G", cancellationToken).ConfigureAwait(false);
    }

    public ValueTask ParkAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException("The verified Prosistel profile does not advertise Park."));

    public ValueTask DisposeAsync() => _transport.DisposeAsync();

    private async ValueTask SendAsync(string payload, CancellationToken cancellationToken) =>
        await _transport.WriteAsync(Ascii.GetBytes($"\x02{payload}\r"), cancellationToken).ConfigureAwait(false);

    private async ValueTask ReadAcknowledgementAsync(string operation, CancellationToken cancellationToken)
    {
        while (true)
        {
            string[] fields = Split(await ReadFrameAsync(cancellationToken).ConfigureAwait(false));
            if (IsError(fields, operation)) ThrowControllerError(fields);
            if (fields is ["A", var responseOperation, "R"] && responseOperation == operation) return;
            // A position broadcast may already be in flight when CPM is disabled. Ignore it.
        }
    }

    private async ValueTask<string> ReadFrameAsync(CancellationToken cancellationToken)
    {
        var frame = new StringBuilder();
        bool inFrame = false;
        while (frame.Length < 128)
        {
            int read = await _transport.ReadAsync(_oneByte, cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Rotator transport closed while reading a Prosistel frame.");
            byte value = _oneByte[0];
            if (value == Stx)
            {
                frame.Clear();
                inFrame = true;
                continue;
            }
            if (!inFrame) continue;
            if (value == Cr) return frame.ToString();
            frame.Append((char)value);
        }
        throw new InvalidDataException("Prosistel response exceeded the 128-byte frame limit.");
    }

    private static string[] Split(string frame) => frame.Split(',', StringSplitOptions.None);
    private static bool IsError(string[] fields, string operation) =>
        fields.Length >= 3 && fields[0] == "A" && fields[1] == operation && fields[2] == "E";

    private static void ThrowControllerError(string[] fields)
    {
        string operation = fields.Length > 1 ? fields[1] : "unknown";
        string code = fields.Length > 3 ? fields[3] : "unspecified";
        throw new InvalidOperationException($"Prosistel operation '{operation}' failed with controller error {code}.");
    }
}
