namespace Rig2Cast.Abstractions.Drivers;

public enum RadioSerialParity
{
    None,
    Odd,
    Even,
    Mark,
    Space
}

public enum RadioSerialStopBits
{
    One,
    OnePointFive,
    Two
}

public enum RadioSerialHandshake
{
    None,
    XOnXOff,
    RequestToSend,
    RequestToSendXOnXOff
}

public sealed record SerialSetting<T>(
    T DefaultValue,
    bool UserConfigurable = false,
    IReadOnlySet<T>? AllowedValues = null);

public sealed record SerialConnectionProfile(
    SerialSetting<int> DataBits,
    SerialSetting<RadioSerialParity> Parity,
    SerialSetting<RadioSerialStopBits> StopBits,
    SerialSetting<RadioSerialHandshake> Handshake,
    SerialSetting<bool> DtrEnable,
    SerialSetting<bool> RtsEnable,
    SerialSetting<TimeSpan> ReadTimeout,
    SerialSetting<TimeSpan> WriteTimeout)
{
    public static SerialConnectionProfile Resolve(RadioModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.SerialProfile is not null)
            return model.SerialProfile;
        IReadOnlyDictionary<string, string> settings = model.DefaultConnectionSettings ??
            new Dictionary<string, string>();
        return Create(
            GetInt(settings, "serial.dataBits", 8),
            GetEnum(settings, "serial.parity", RadioSerialParity.None),
            GetEnum(settings, "serial.stopBits", RadioSerialStopBits.One),
            GetEnum(settings, "serial.handshake", RadioSerialHandshake.None),
            GetBool(settings, "serial.dtrEnable"),
            GetBool(settings, "serial.rtsEnable"));
    }

    public static SerialConnectionProfile Create(
        int dataBits = 8,
        RadioSerialParity parity = RadioSerialParity.None,
        RadioSerialStopBits stopBits = RadioSerialStopBits.One,
        RadioSerialHandshake handshake = RadioSerialHandshake.None,
        bool dtrEnable = false,
        bool rtsEnable = false) =>
        new(
            new(dataBits),
            new(parity),
            new(stopBits),
            new(handshake),
            new(dtrEnable, true),
            new(rtsEnable, true),
            new(TimeSpan.FromSeconds(2), true),
            new(TimeSpan.FromSeconds(2), true));

    private static int GetInt(IReadOnlyDictionary<string, string> settings, string key, int fallback) =>
        settings.TryGetValue(key, out string? value) && int.TryParse(value, out int parsed) ? parsed : fallback;

    private static bool GetBool(IReadOnlyDictionary<string, string> settings, string key) =>
        settings.TryGetValue(key, out string? value) && bool.TryParse(value, out bool parsed) && parsed;

    private static T GetEnum<T>(IReadOnlyDictionary<string, string> settings, string key, T fallback)
        where T : struct, Enum =>
        settings.TryGetValue(key, out string? value) && Enum.TryParse(value, true, out T parsed) ? parsed : fallback;
}

public sealed record SerialConnectionSettings(
    string PortName,
    int BaudRate,
    int DataBits,
    RadioSerialParity Parity,
    RadioSerialStopBits StopBits,
    RadioSerialHandshake Handshake,
    bool DtrEnable,
    bool RtsEnable,
    TimeSpan ReadTimeout,
    TimeSpan WriteTimeout)
{
    public static SerialConnectionSettings FromModel(
        RadioModelDescriptor model,
        string portName,
        int? baudRate = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        SerialConnectionProfile profile = SerialConnectionProfile.Resolve(model);
        int effectiveBaud = baudRate ?? model.DefaultBaudRate ??
            throw new InvalidOperationException($"Model '{model.Id}' has no default baud rate.");
        return new(
            portName,
            effectiveBaud,
            profile.DataBits.DefaultValue,
            profile.Parity.DefaultValue,
            profile.StopBits.DefaultValue,
            profile.Handshake.DefaultValue,
            profile.DtrEnable.DefaultValue,
            profile.RtsEnable.DefaultValue,
            profile.ReadTimeout.DefaultValue,
            profile.WriteTimeout.DefaultValue);
    }
}
