using Rig2Cast.Abstractions.Transports;
using Rig2Cast.Protocols.Ascii;

namespace Rig2Cast.Drivers.Yaesu.Protocol;

public sealed class YaesuAsciiProtocol : IAsyncDisposable
{
    private static readonly AsciiCatSessionOptions Options = new()
    {
        ProtocolName = "Yaesu",
        FrameCommand = Frame,
        ValidateResponsePrefix = ValidatePrefix,
        InvalidFrameException = static message => new YaesuProtocolException(message)
    };

    private readonly AsciiCatSession _session;

    public YaesuAsciiProtocol(IRadioTransport transport, TimeSpan? responseTimeout = null) =>
        _session = new AsciiCatSession(transport, Options, responseTimeout);

    public int DroppedUnsolicitedFrameCount => _session.DroppedUnsolicitedFrameCount;

    public int ConsumeDroppedUnsolicitedFrameCount() => _session.ConsumeDroppedUnsolicitedFrameCount();

    public ValueTask SendAsync(string command, CancellationToken cancellationToken = default) =>
        _session.SendAsync(command, cancellationToken);

    public ValueTask<string> QueryAsync(
        string command,
        string expectedResponsePrefix,
        CancellationToken cancellationToken = default) =>
        _session.QueryAsync(command, expectedResponsePrefix, cancellationToken);

    public ValueTask<string> QueryAsync(
        string command,
        string expectedResponsePrefix,
        Func<string, bool> responseValidator,
        CancellationToken cancellationToken = default) =>
        _session.QueryAsync(command, expectedResponsePrefix, responseValidator, cancellationToken);

    public IAsyncEnumerable<string> WatchUnsolicitedFramesAsync(CancellationToken cancellationToken = default) =>
        _session.WatchUnsolicitedFramesAsync(cancellationToken);

    public ValueTask DisposeAsync() => _session.DisposeAsync();

    public static string Frame(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        string value = command.EndsWith(';') ? command : $"{command};";
        foreach (char character in value)
        {
            if (character > 0x7f || char.IsControl(character))
                throw new ArgumentException(
                    "Yaesu CAT commands must contain printable ASCII characters only.", nameof(command));
        }
        if (value[..^1].Contains(';', StringComparison.Ordinal))
            throw new ArgumentException(
                "A CAT command may contain only its final semicolon terminator.", nameof(command));
        return value.ToUpperInvariant();
    }

    private static void ValidatePrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (prefix.Length is < 2 or > 16 ||
            prefix.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException(
                "A Yaesu response prefix must contain 2-16 ASCII letters or digits.", nameof(prefix));
        }
    }
}
