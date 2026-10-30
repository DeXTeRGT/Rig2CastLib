using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Rig2Cast.Rotators;

public sealed partial class Gs232RotatorDriverFactory : IRotatorDriverFactory
{
    public RotatorModelDescriptor Model { get; } = new(
        "yaesu.gs232", "Yaesu", "GS-232 compatible", ["Serial", "Tcp"], 9600);

    public async ValueTask<IRotatorDriver> OpenAsync(IRotatorTransport transport,
        IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken)
    {
        await transport.OpenAsync(cancellationToken).ConfigureAwait(false);
        double maximum = settings.TryGetValue("maximumAzimuth", out string? configured) &&
                         double.TryParse(configured, CultureInfo.InvariantCulture, out double value) ? value : 450;
        return new Gs232RotatorDriver(transport, maximum);
    }
}

public sealed partial class Gs232RotatorDriver(IRotatorTransport transport, double maximumAzimuth) : IRotatorDriver
{
    private static readonly Encoding Ascii = Encoding.ASCII;
    public RotatorCapabilities Capabilities { get; } = new(
        RotatorAxis.Azimuth, 0, maximumAzimuth, true, true, true, true, false);

    public async ValueTask<RotatorPosition> ReadPositionAsync(CancellationToken cancellationToken)
    {
        await SendAsync("C\r", cancellationToken).ConfigureAwait(false);
        string response = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        Match match = PositionPattern().Match(response);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out double azimuth))
            throw new InvalidDataException($"Unrecognized GS-232 position response '{response}'.");
        return new RotatorPosition(azimuth);
    }

    public ValueTask MoveToAsync(RotatorPosition position, CancellationToken cancellationToken)
    {
        if (position.Azimuth < Capabilities.MinimumAzimuth || position.Azimuth > Capabilities.MaximumAzimuth)
            throw new ArgumentOutOfRangeException(nameof(position));
        return SendAsync($"M{Math.Round(position.Azimuth):000}\r", cancellationToken);
    }
    public ValueTask StartAsync(RotatorDirection direction, CancellationToken cancellationToken) =>
        SendAsync(direction == RotatorDirection.Clockwise ? "R\r" : "L\r", cancellationToken);
    public ValueTask StopAsync(CancellationToken cancellationToken) => SendAsync("S\r", cancellationToken);
    public ValueTask ParkAsync(CancellationToken cancellationToken) => throw new NotSupportedException("This GS-232 profile does not advertise Park.");
    public ValueTask DisposeAsync() => transport.DisposeAsync();

    private async ValueTask SendAsync(string command, CancellationToken cancellationToken) =>
        await transport.WriteAsync(Ascii.GetBytes(command), cancellationToken).ConfigureAwait(false);

    private async ValueTask<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        byte[] one = new byte[1];
        var result = new StringBuilder();
        while (result.Length < 128)
        {
            int read = await transport.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Rotator transport closed while reading a response.");
            char value = (char)one[0];
            if (value is '\r' or '\n') { if (result.Length > 0) break; else continue; }
            result.Append(value);
        }
        return result.ToString();
    }

    [GeneratedRegex(@"(?:AZ=|[+\-]?)(\d{1,4}(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex PositionPattern();
}
