using System.Threading.Channels;
using Rig2Cast.Protocols.Civ;

namespace Rig2Cast.Simulator.Civ;

/// <summary>
/// Deterministic radio-side CI-V fixture for the implemented IC-7300 command surface.
/// </summary>
public sealed class CivRadioSimulator : IAsyncDisposable
{
    private readonly InMemoryRadioTransport _transport;
    private readonly CivSimulatorOptions _options;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _stateGate = new();
    private readonly Task _runLoop;
    private long _frequencyHz;
    private long _backgroundFrequencyHz;
    private byte _mode;
    private byte _backgroundMode;
    private byte _filter;
    private byte _backgroundFilter;
    private byte _passbandCode;
    private bool _dataMode;
    private bool _backgroundDataMode;
    private byte _activeVfo;
    private bool _split;
    private bool _transmitting;
    private readonly Dictionary<byte, int> _levels = new()
    {
        [0x01] = 128, [0x02] = 128, [0x03] = 0, [0x06] = 64,
        [0x09] = 128, [0x0A] = 64, [0x0B] = 128, [0x0C] = 128,
        [0x0F] = 0, [0x12] = 64, [0x15] = 0, [0x16] = 0,
        [0x17] = 0, [0x19] = 128
    };
    private readonly Dictionary<byte, int> _meters = new()
    {
        [0x02] = 120, [0x11] = 0, [0x12] = 0, [0x13] = 0
    };
    private readonly Dictionary<(byte Command, byte Subcommand), bool> _switches = new()
    {
        [(0x16, 0x22)] = false, [(0x16, 0x40)] = false,
        [(0x16, 0x41)] = false, [(0x16, 0x44)] = false, [(0x16, 0x48)] = false,
        [(0x16, 0x50)] = false,
        [(0x1C, 0x01)] = false, [(0x21, 0x01)] = false, [(0x21, 0x02)] = false
    };
    private byte _attenuator;
    private byte _preamp;
    private byte _agc = 0x02;
    private int _clarifierOffsetHz;
    private int _nextResponse;
    private int _disposed;

    public CivRadioSimulator(InMemoryRadioTransport transport, CivSimulatorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (!transport.IsConnected)
            throw new InvalidOperationException("The in-memory transport must be connected before starting the CI-V simulator.");

        _options = options ?? new CivSimulatorOptions();
        if (_options.InitialFrequencyHz < 0 || _options.InitialBackgroundFrequencyHz < 0 ||
            _options.InitialActiveVfo > 1 || !IsPassbandCode(_options.InitialPassbandCode) ||
            _options.ResponseFragmentLength <= 0 ||
            _options.ResponseDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "CI-V simulator options are invalid.");
        }
        _ = new CivFrame(_options.RadioAddress, _options.ControllerAddress, [0x03]);

        _transport = transport;
        _frequencyHz = _options.InitialFrequencyHz;
        _backgroundFrequencyHz = _options.InitialBackgroundFrequencyHz;
        _mode = _options.InitialMode;
        _backgroundMode = _options.InitialBackgroundMode;
        _filter = _options.InitialFilter;
        _backgroundFilter = _options.InitialBackgroundFilter;
        _dataMode = _options.InitialDataMode;
        _backgroundDataMode = _options.InitialBackgroundDataMode;
        _activeVfo = _options.InitialActiveVfo;
        _passbandCode = _options.InitialPassbandCode;
        _split = _options.InitialSplit;
        _transmitting = _options.InitialTransmitting;
        _runLoop = RunAsync();
    }

    public CivSimulatorOptions Options => _options;

    public void SetNextResponse(CivSimulatorNextResponse response) =>
        Interlocked.Exchange(ref _nextResponse, (int)response);

    public async ValueTask EmitFrequencyTransceiveAsync(
        long frequencyHz,
        CancellationToken cancellationToken = default)
    {
        lock (_stateGate)
            _frequencyHz = frequencyHz;
        byte[] bcd = CivBcd.Encode(frequencyHz, 5);
        await SendFrameAsync(
            new CivFrame(0x00, _options.RadioAddress, Prepend(0x00, bcd)), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask EmitModeTransceiveAsync(
        byte mode,
        byte filter,
        CancellationToken cancellationToken = default)
    {
        lock (_stateGate)
        {
            _mode = mode;
            _filter = filter;
        }
        await SendFrameAsync(
            new CivFrame(0x00, _options.RadioAddress, [0x01, mode, filter]), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await _runLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        _stopping.Dispose();
    }

    private async Task RunAsync()
    {
        var decoder = new CivFrameDecoder();
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                byte[] bytes = await _transport.ReadDriverCommandAsync(_stopping.Token).ConfigureAwait(false);
                foreach (CivFrame command in decoder.Append(bytes))
                    await HandleCommandAsync(command, bytes, _stopping.Token).ConfigureAwait(false);
            }
        }
        catch (ChannelClosedException)
        {
            // The driver owns and may close the shared transport before the
            // radio-side simulator lifetime is disposed.
        }
    }

    private async ValueTask HandleCommandAsync(
        CivFrame command,
        byte[] encodedCommand,
        CancellationToken cancellationToken)
    {
        if (_options.EchoCommands)
            await SendBytesAsync(encodedCommand, cancellationToken).ConfigureAwait(false);

        CivSimulatorNextResponse behavior = (CivSimulatorNextResponse)Interlocked.Exchange(
            ref _nextResponse, (int)CivSimulatorNextResponse.Normal);
        if (behavior == CivSimulatorNextResponse.Drop)
            return;
        if (behavior == CivSimulatorNextResponse.Close)
        {
            await _transport.SendRadioResponseAsync(ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_options.ResponseDelay > TimeSpan.Zero)
            await Task.Delay(_options.ResponseDelay, cancellationToken).ConfigureAwait(false);

        CivFrame response;
        if (behavior == CivSimulatorNextResponse.Reject ||
            command.Destination != _options.RadioAddress ||
            command.Source != _options.ControllerAddress)
        {
            response = Reply([CivSession.NegativeAcknowledgement]);
        }
        else
        {
            response = BuildResponse(command);
        }
        if (_options.SupportsXieguIdentity && command.Message.Span is [0x11, 0x00 or 0x20])
            return;
        await SendFrameAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private CivFrame BuildResponse(CivFrame command)
    {
        ReadOnlySpan<byte> message = command.Message.Span;
        if (message.Length == 6 && message[0] == 0x05 &&
            CivBcd.TryDecode(message[1..], out long frequency) && frequency <= 74_800_000)
        {
            lock (_stateGate)
                _frequencyHz = frequency;
            return Reply([CivSession.Acknowledgement]);
        }
        if (_options.SupportsXieguExtendedVfo && message.Length == 7 && message[0] == 0x25 &&
            message[1] is 0x00 or 0x01 &&
            CivBcd.TryDecode(message[2..], out long targetedFrequency) && targetedFrequency <= 74_800_000)
        {
            WriteRelativeVfoFrequency(message[1], targetedFrequency);
            return Reply([CivSession.Acknowledgement]);
        }
        if (_options.SupportsXieguExtendedVfo && message.Length == 5 && message[0] == 0x26 &&
            message[1] is 0x00 or 0x01 && IsSupportedMode(message[2]) &&
            message[3] is 0x00 or 0x01 && message[4] is >= 0x01 and <= 0x03)
        {
            WriteRelativeVfoMode(message[1], message[2], message[3] == 0x01, message[4]);
            return Reply([CivSession.Acknowledgement]);
        }
        if (_options.SupportsXieguExtendedVfo && message.Length == 2 && message[0] == 0x07 &&
            message[1] is 0x00 or 0x01)
        {
            SelectVfo(message[1]);
            return Reply([CivSession.Acknowledgement]);
        }
        if (message.Length is 2 or 3 && message[0] == 0x06 && IsSupportedMode(message[1]) &&
            (message.Length == 2 || message[2] is >= 0x01 and <= 0x03))
        {
            lock (_stateGate)
            {
                _mode = message[1];
                _filter = message.Length == 3 ? message[2] : (byte)0x01;
            }
            return Reply([CivSession.Acknowledgement]);
        }
        if (message.Length == 2 && message[0] == 0x0F && message[1] is 0x00 or 0x01)
        {
            lock (_stateGate)
                _split = message[1] == 0x01;
            return Reply([CivSession.Acknowledgement]);
        }
        if (message.Length == 3 && message[0] == 0x1C && message[1] == 0x00 &&
            message[2] is 0x00 or 0x01)
        {
            lock (_stateGate)
                _transmitting = message[2] == 0x01;
            return Reply([CivSession.Acknowledgement]);
        }
        if (message.Length == 3 && message[0] == 0x1C && message[1] == 0x01 &&
            message[2] is 0x00 or 0x01)
        {
            lock (_stateGate)
                _switches[(0x1C, 0x01)] = message[2] == 0x01;
            return Reply([CivSession.Acknowledgement]);
        }
        if (message.Length == 3 && message[0] == 0x1A && message[1] == 0x03 &&
            IsPassbandCode(message[2]))
        {
            lock (_stateGate)
                _passbandCode = message[2];
            return Reply([CivSession.Acknowledgement]);
        }
        if (message.Length == 4 && message[0] == 0x1A && message[1] == 0x06 &&
            message[2] is 0x00 or 0x01 &&
            (message[2] == 0x00 ? message[3] == 0x00 : message[3] is >= 0x01 and <= 0x03))
        {
            lock (_stateGate)
            {
                _dataMode = message[2] == 0x01;
                if (_dataMode)
                    _filter = message[3];
            }
            return Reply([CivSession.Acknowledgement]);
        }
        if (message.Length == 4 && message[0] == 0x14 && _levels.ContainsKey(message[1]) &&
            CivBcd.TryDecodeBigEndian(message[2..], out long level) && level <= 255)
        {
            lock (_stateGate)
                _levels[message[1]] = (int)level;
            return Reply([CivSession.Acknowledgement]);
        }
        if (message.Length == 3 && _switches.ContainsKey((message[0], message[1])) &&
            message[2] is 0x00 or 0x01)
        {
            lock (_stateGate)
                _switches[(message[0], message[1])] = message[2] == 0x01;
            return Reply([CivSession.Acknowledgement]);
        }
        if (message.Length == 2 && message[0] == 0x11 && message[1] is 0x00 or 0x20)
        {
            lock (_stateGate)
                _attenuator = _options.SupportsXieguIdentity
                    ? (_attenuator == 0 ? (byte)0x0C : (byte)0x00)
                    : message[1];
            return Reply([CivSession.Acknowledgement]);
        }
        if (message.Length == 3 && message[0] == 0x16 && message[1] == 0x02 && message[2] <= 0x02)
        {
            lock (_stateGate)
                _preamp = message[2];
            return Reply([CivSession.Acknowledgement]);
        }
        if (message.Length == 3 && message[0] == 0x16 && message[1] == 0x12 &&
            message[2] <= 0x03)
        {
            lock (_stateGate)
                _agc = message[2];
            return Reply([CivSession.Acknowledgement]);
        }
        if (message.Length == 5 && message[0] == 0x21 && message[1] == 0x00 &&
            CivBcd.TryDecode(message[2..4], out long offset) && offset <= 9_999 &&
            message[4] is 0x00 or 0x01)
        {
            lock (_stateGate)
                _clarifierOffsetHz = (int)(message[4] == 0x01 ? -offset : offset);
            return Reply([CivSession.Acknowledgement]);
        }
        if (_options.SupportsStandardIdentity && message.SequenceEqual(new byte[] { 0x19, 0x00 }))
            return Reply([0x19, 0x00, _options.RadioAddress]);
        if (_options.SupportsXieguIdentity && message.SequenceEqual(new byte[] { 0x1D, 0x19 }))
            return Reply([0x1D, 0x19, 0x00, 0x90]);
        if (message.SequenceEqual(new byte[] { 0x1C, 0x00 }))
            return Reply([0x1C, 0x00, ReadTransmitting() ? (byte)0x01 : (byte)0x00]);
        if (_options.SupportsXieguExtendedVfo && message.Length == 2 && message[0] == 0x25 &&
            message[1] is 0x00 or 0x01)
            return Reply([0x25, ReadActiveVfo(), .. CivBcd.Encode(ReadRelativeVfoFrequency(message[1]), 5)]);
        if (_options.SupportsXieguExtendedVfo && message.Length == 2 && message[0] == 0x26 &&
            message[1] is 0x00 or 0x01)
        {
            (byte mode, bool dataMode, byte filter) = ReadRelativeVfoMode(message[1]);
            return Reply([0x26, ReadActiveVfo(), mode, dataMode ? (byte)0x01 : (byte)0x00, filter]);
        }
        if (message.SequenceEqual(new byte[] { 0x1A, 0x03 }))
            return Reply([0x1A, 0x03, ReadPassbandCode()]);
        if (message.SequenceEqual(new byte[] { 0x1A, 0x06 }))
        {
            (bool enabled, byte filter) = ReadDataMode();
            return Reply([0x1A, 0x06, enabled ? (byte)0x01 : (byte)0x00, enabled ? filter : (byte)0x00]);
        }
        if (message.Length == 2 && message[0] == 0x14 && ReadLevel(message[1]) is int currentLevel)
            return Reply([0x14, message[1], .. EncodeLevel(currentLevel)]);
        if (message.Length == 2 && message[0] == 0x15 && ReadMeter(message[1]) is int currentMeter)
            return Reply([0x15, message[1], .. EncodeLevel(currentMeter)]);
        if (message.Length == 2 && ReadSwitch(message[0], message[1]) is bool currentSwitch)
            return Reply([message[0], message[1], currentSwitch ? (byte)0x01 : (byte)0x00]);
        if (message.SequenceEqual(new byte[] { 0x11 }))
            return Reply([0x11, ReadAttenuator()]);
        if (message.SequenceEqual(new byte[] { 0x16, 0x02 }))
            return Reply([0x16, 0x02, ReadPreamp()]);
        if (message.SequenceEqual(new byte[] { 0x16, 0x12 }))
            return Reply([0x16, 0x12, ReadAgc()]);
        if (message.SequenceEqual(new byte[] { 0x21, 0x00 }))
        {
            int currentOffset = ReadClarifierOffset();
            return Reply([0x21, 0x00, .. CivBcd.Encode(Math.Abs((long)currentOffset), 2),
                currentOffset < 0 ? (byte)0x01 : (byte)0x00]);
        }
        if (message.Length != 1)
            return Reply([CivSession.NegativeAcknowledgement]);

        return message[0] switch
        {
            0x03 => Reply(Prepend(0x03, CivBcd.Encode(ReadFrequency(), 5))),
            0x04 => Reply([0x04, ReadMode(), ReadFilter()]),
            0x0F => Reply([0x0F, ReadSplit() ? (byte)0x01 : (byte)0x00]),
            _ => Reply([CivSession.NegativeAcknowledgement])
        };
    }

    private CivFrame Reply(ReadOnlySpan<byte> message) =>
        new(_options.ControllerAddress, _options.RadioAddress, message);

    private async ValueTask SendFrameAsync(CivFrame frame, CancellationToken cancellationToken) =>
        await SendBytesAsync(CivFrameCodec.Encode(frame), cancellationToken).ConfigureAwait(false);

    private async ValueTask SendBytesAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        for (int offset = 0; offset < bytes.Length; offset += _options.ResponseFragmentLength)
        {
            int count = Math.Min(_options.ResponseFragmentLength, bytes.Length - offset);
            await _transport.SendRadioResponseAsync(
                bytes.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }
    }

    private long ReadFrequency()
    {
        lock (_stateGate)
            return _frequencyHz;
    }

    private byte ReadMode()
    {
        lock (_stateGate)
            return _mode;
    }

    private byte ReadFilter()
    {
        lock (_stateGate)
            return _filter;
    }

    private bool ReadSplit()
    {
        lock (_stateGate)
            return _split;
    }

    private bool ReadTransmitting()
    {
        lock (_stateGate)
            return _transmitting;
    }

    private byte ReadPassbandCode()
    {
        lock (_stateGate)
            return _passbandCode;
    }

    private (bool Enabled, byte Filter) ReadDataMode()
    {
        lock (_stateGate)
            return (_dataMode, _filter);
    }

    private int? ReadLevel(byte subcommand)
    {
        lock (_stateGate)
            return _levels.TryGetValue(subcommand, out int value) ? value : null;
    }

    private int? ReadMeter(byte subcommand)
    {
        lock (_stateGate)
            return _meters.TryGetValue(subcommand, out int value) ? value : null;
    }

    private bool? ReadSwitch(byte command, byte subcommand)
    {
        lock (_stateGate)
            return _switches.TryGetValue((command, subcommand), out bool value) ? value : null;
    }

    private byte ReadAttenuator()
    {
        lock (_stateGate)
            return _attenuator;
    }

    private byte ReadPreamp()
    {
        lock (_stateGate)
            return _preamp;
    }

    private byte ReadAgc()
    {
        lock (_stateGate)
            return _agc;
    }

    private int ReadClarifierOffset()
    {
        lock (_stateGate)
            return _clarifierOffsetHz;
    }

    private byte ReadActiveVfo()
    {
        lock (_stateGate)
            return _activeVfo;
    }

    private long ReadRelativeVfoFrequency(byte relativeSelector)
    {
        lock (_stateGate)
            return relativeSelector == 0 ? _frequencyHz : _backgroundFrequencyHz;
    }

    private (byte Mode, bool DataMode, byte Filter) ReadRelativeVfoMode(byte relativeSelector)
    {
        lock (_stateGate)
            return relativeSelector == 0
                ? (_mode, _dataMode, _filter)
                : (_backgroundMode, _backgroundDataMode, _backgroundFilter);
    }

    private void WriteRelativeVfoFrequency(byte relativeSelector, long frequencyHz)
    {
        lock (_stateGate)
        {
            if (relativeSelector == 0) _frequencyHz = frequencyHz;
            else _backgroundFrequencyHz = frequencyHz;
        }
    }

    private void WriteRelativeVfoMode(byte relativeSelector, byte mode, bool dataMode, byte filter)
    {
        lock (_stateGate)
        {
            if (relativeSelector == 0)
            {
                _mode = mode;
                _dataMode = dataMode;
                _filter = filter;
            }
            else
            {
                _backgroundMode = mode;
                _backgroundDataMode = dataMode;
                _backgroundFilter = filter;
            }
        }
    }

    private void SelectVfo(byte activeVfo)
    {
        lock (_stateGate)
        {
            if (_activeVfo == activeVfo) return;
            (_frequencyHz, _backgroundFrequencyHz) = (_backgroundFrequencyHz, _frequencyHz);
            (_mode, _backgroundMode) = (_backgroundMode, _mode);
            (_dataMode, _backgroundDataMode) = (_backgroundDataMode, _dataMode);
            (_filter, _backgroundFilter) = (_backgroundFilter, _filter);
            _activeVfo = activeVfo;
        }
    }

    private byte[] EncodeLevel(int value) => _options.SupportsXieguIdentity
        ? [(byte)(value >> 8), (byte)value]
        : CivBcd.EncodeBigEndian(value, 2);

    private static byte[] Prepend(byte value, byte[] tail)
    {
        var result = new byte[tail.Length + 1];
        result[0] = value;
        tail.CopyTo(result, 1);
        return result;
    }

    private static bool IsSupportedMode(byte mode) =>
        mode is 0x00 or 0x01 or 0x02 or 0x03 or 0x04 or 0x05 or 0x07 or 0x08;

    private static bool IsPassbandCode(byte value) =>
        (value & 0x0F) <= 9 && (value >> 4) <= 4 && (((value >> 4) * 10) + (value & 0x0F)) <= 49;
}
