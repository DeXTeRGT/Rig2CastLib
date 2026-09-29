using System.Text.Json;

namespace Rig2Cast.WebGui;

public sealed class StationConfiguration
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip };
    public StationIdentity Station { get; set; } = new();
    public StationRadio Radio { get; set; } = new();
    public StationAudio Audio { get; set; } = new();
    public StationAccess Access { get; set; } = new();
    public bool IsLocked => Station.Locked;

    public static StationConfiguration Load(IConfiguration configuration)
    {
        string? path = configuration["Rig2Cast:StationConfig"];
        if (string.IsNullOrWhiteSpace(path)) return new StationConfiguration();
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Station configuration was not found.", fullPath);
        StationConfiguration result = JsonSerializer.Deserialize<StationConfiguration>(
            File.ReadAllText(fullPath), JsonOptions)
            ?? throw new InvalidDataException("Station configuration contains JSON null.");
        result.Validate(fullPath);
        return result;
    }

    public ConnectRequest RadioRequest(string clientId) => new(
        clientId, Radio.ModelId, Radio.Transport, Radio.SerialPort, Radio.BaudRate,
        Radio.TcpHost, Radio.TcpPort, Radio.Settings, Access.AllowRadioWrites,
        Radio.ModeRestrictions);

    private void Validate(string path)
    {
        if (!Station.Locked) throw new InvalidDataException($"Station configuration '{path}' must set Station.Locked=true.");
        ArgumentException.ThrowIfNullOrWhiteSpace(Station.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(Radio.ModelId);
        if (!Enum.TryParse<Rig2Cast.Abstractions.Drivers.RadioTransportKind>(Radio.Transport, true, out _))
            throw new InvalidDataException($"Unsupported station radio transport '{Radio.Transport}'.");
        ArgumentException.ThrowIfNullOrWhiteSpace(Audio.Host);
        if (Audio.ServerTxPort is < 1 or > 65535 || Audio.ServerRxPort is < 1 or > 65535)
            throw new InvalidDataException("Station audio ports must be between 1 and 65535.");
        if (Audio.ServerTxPort == Audio.ServerRxPort) throw new InvalidDataException("Station audio TX and RX ports must differ.");
        if (Audio.OpusBitrate is < 6000 or > 128000) throw new InvalidDataException("Station Opus bitrate must be between 6000 and 128000.");
        if (!Audio.ConnectionPolicy.Equals("OnDemand", StringComparison.OrdinalIgnoreCase) &&
            !Audio.ConnectionPolicy.Equals("AlwaysConnected", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Station Audio.ConnectionPolicy must be OnDemand or AlwaysConnected.");
        if (Audio.DisconnectGraceSeconds is < 0 or > 300)
            throw new InvalidDataException("Station audio disconnect grace must be between 0 and 300 seconds.");
    }
}

public sealed class StationIdentity
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "Rig2Cast Station";
    public bool Locked { get; set; }
    public bool AutoConnect { get; set; } = true;
}

public sealed class StationRadio
{
    public string ModelId { get; set; } = "";
    public string Transport { get; set; } = "Serial";
    public string? SerialPort { get; set; }
    public int? BaudRate { get; set; }
    public string? TcpHost { get; set; }
    public int? TcpPort { get; set; }
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string ModeRestrictions { get; set; } = "Enforce";
}

public sealed class StationAudio
{
    public string Host { get; set; } = "127.0.0.1";
    public int ServerTxPort { get; set; } = 6001;
    public int ServerRxPort { get; set; } = 6002;
    public int OpusBitrate { get; set; } = 16000;
    public bool AllowMicrophone { get; set; } = true;
    public string ConnectionPolicy { get; set; } = "OnDemand";
    public int DisconnectGraceSeconds { get; set; } = 15;
}

public sealed class StationAccess
{
    public bool AllowRadioWrites { get; set; } = true;
    public bool AllowPtt { get; set; } = true;
    public bool AllowAudioReceive { get; set; } = true;
    public bool AllowAudioTransmit { get; set; } = true;
}
