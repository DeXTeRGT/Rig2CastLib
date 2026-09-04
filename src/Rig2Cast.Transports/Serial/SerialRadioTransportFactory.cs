using System.IO.Ports;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Transports;

namespace Rig2Cast.Transports.Serial;

public static class SerialRadioTransportFactory
{
    public static IRadioTransport Create(
        RadioModelDescriptor model,
        SerialConnectionSettings settings,
        bool allowUnsafeOverride = false) =>
        new SerialRadioTransport(CreateOptions(model, settings, allowUnsafeOverride));

    public static SerialRadioTransportOptions CreateOptions(
        RadioModelDescriptor model,
        SerialConnectionSettings settings,
        bool allowUnsafeOverride = false)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(settings);
        if (!model.SupportedTransports.Contains(RadioTransportKind.Serial))
            throw new NotSupportedException($"Model '{model.Id}' does not support serial transport.");
        SerialConnectionProfile profile = SerialConnectionProfile.Resolve(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.PortName);

        if (!allowUnsafeOverride && !model.SupportedBaudRates.Contains(settings.BaudRate))
            throw new ArgumentOutOfRangeException(nameof(settings), settings.BaudRate,
                $"Baud rate is not supported by model '{model.Id}'.");
        ValidateSetting(profile.DataBits, settings.DataBits, nameof(settings.DataBits), allowUnsafeOverride);
        ValidateSetting(profile.Parity, settings.Parity, nameof(settings.Parity), allowUnsafeOverride);
        ValidateSetting(profile.StopBits, settings.StopBits, nameof(settings.StopBits), allowUnsafeOverride);
        ValidateSetting(profile.Handshake, settings.Handshake, nameof(settings.Handshake), allowUnsafeOverride);
        ValidateSetting(profile.DtrEnable, settings.DtrEnable, nameof(settings.DtrEnable), allowUnsafeOverride);
        ValidateSetting(profile.RtsEnable, settings.RtsEnable, nameof(settings.RtsEnable), allowUnsafeOverride);
        ValidateSetting(profile.ReadTimeout, settings.ReadTimeout, nameof(settings.ReadTimeout), allowUnsafeOverride);
        ValidateSetting(profile.WriteTimeout, settings.WriteTimeout, nameof(settings.WriteTimeout), allowUnsafeOverride);

        if (settings.DataBits is < 5 or > 8)
            throw new ArgumentOutOfRangeException(nameof(settings), settings.DataBits, "Data bits must be from 5 through 8.");
        if (settings.BaudRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(settings), settings.BaudRate, "Baud rate must be positive.");
        if (settings.ReadTimeout <= TimeSpan.Zero || settings.WriteTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(settings), "Serial timeouts must be positive.");

        return new SerialRadioTransportOptions
        {
            PortName = settings.PortName,
            BaudRate = settings.BaudRate,
            DataBits = settings.DataBits,
            Parity = Map(settings.Parity),
            StopBits = Map(settings.StopBits),
            Handshake = Map(settings.Handshake),
            DtrEnable = settings.DtrEnable,
            RtsEnable = settings.RtsEnable,
            ReadTimeout = settings.ReadTimeout,
            WriteTimeout = settings.WriteTimeout
        };
    }

    private static void ValidateSetting<T>(
        SerialSetting<T> descriptor, T value, string name, bool allowUnsafeOverride)
    {
        if (allowUnsafeOverride || EqualityComparer<T>.Default.Equals(value, descriptor.DefaultValue))
            return;
        if (!descriptor.UserConfigurable)
            throw new ArgumentException($"Serial setting '{name}' is fixed by the selected model.", nameof(value));
        if (descriptor.AllowedValues is not null && !descriptor.AllowedValues.Contains(value))
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"Serial setting '{name}' is outside the values allowed by the selected model.");
    }

    private static Parity Map(RadioSerialParity value) => value switch
    {
        RadioSerialParity.None => Parity.None,
        RadioSerialParity.Odd => Parity.Odd,
        RadioSerialParity.Even => Parity.Even,
        RadioSerialParity.Mark => Parity.Mark,
        RadioSerialParity.Space => Parity.Space,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static StopBits Map(RadioSerialStopBits value) => value switch
    {
        RadioSerialStopBits.One => StopBits.One,
        RadioSerialStopBits.OnePointFive => StopBits.OnePointFive,
        RadioSerialStopBits.Two => StopBits.Two,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static Handshake Map(RadioSerialHandshake value) => value switch
    {
        RadioSerialHandshake.None => Handshake.None,
        RadioSerialHandshake.XOnXOff => Handshake.XOnXOff,
        RadioSerialHandshake.RequestToSend => Handshake.RequestToSend,
        RadioSerialHandshake.RequestToSendXOnXOff => Handshake.RequestToSendXOnXOff,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}
