using Rig2Cast.Abstractions.Transports;
using Rig2Cast.Protocols.Ascii;

namespace Rig2Cast.Drivers.Elecraft.Protocol;

public sealed class ElecraftAsciiProtocol : IAsyncDisposable
{
    private static readonly AsciiCatSessionOptions Options = new()
    {
        ProtocolName = "Elecraft",
        FrameCommand = Frame,
        ValidateResponsePrefix = ValidatePrefix,
        InvalidFrameException = static message => new ElecraftProtocolException(message),
        CommandRejection = static (frame, command) =>
            frame == "?;" ? new ElecraftCommandRejectedException(command) : null
    };

    private readonly AsciiCatSession _session;

    public ElecraftAsciiProtocol(IRadioTransport transport, TimeSpan? responseTimeout = null) =>
        _session = new AsciiCatSession(transport, Options, responseTimeout);

    public int DroppedUnsolicitedFrameCount => _session.DroppedUnsolicitedFrameCount;

    public int ConsumeDroppedUnsolicitedFrameCount() => _session.ConsumeDroppedUnsolicitedFrameCount();

    public ValueTask SendAsync(string command, CancellationToken cancellationToken = default) =>
        _session.SendAsync(command, cancellationToken);

    public ValueTask<string> QueryAsync(
        string command,
        string expectedPrefix,
        CancellationToken cancellationToken = default) =>
        _session.QueryAsync(command, expectedPrefix, cancellationToken);

    public ValueTask<string> QueryAsync(
        string command,
        string expectedPrefix,
        Func<string, bool> responseValidator,
        CancellationToken cancellationToken = default) =>
        _session.QueryAsync(command, expectedPrefix, responseValidator, cancellationToken);

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
                    "Elecraft CAT commands must contain printable ASCII only.", nameof(command));
        }
        if (value[..^1].Contains(';', StringComparison.Ordinal))
            throw new ArgumentException(
                "A CAT command may contain only its final semicolon terminator.", nameof(command));
        return value.ToUpperInvariant();
    }

    private static void ValidatePrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (prefix.Length is < 2 or > 3 ||
            prefix.Any(character => !char.IsAsciiLetter(character) && character != '$'))
        {
            throw new ArgumentException(
                "An Elecraft response prefix must contain two or three command characters.", nameof(prefix));
        }
    }
}
