using System.Text.Json;

namespace Rig2Cast.WebGui;

public sealed class StationConfiguration
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip };
    public StationIdentity Station { get; set; } = new();
    public StationRadio Radio { get; set; } = new();
    public StationAudio Audio { get; set; } = new();
    public StationSpectrum Spectrum { get; set; } = new();
    public StationAccess Access { get; set; } = new();
    public StationRemoteFrequencyPolicy RemoteFrequencyPolicy { get; set; } = new();
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
        if (Spectrum.Enabled)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(Spectrum.SourceHost);
            if (Spectrum.UdpPort is < 1 or > 65535)
                throw new InvalidDataException("Station spectrum UDP port must be between 1 and 65535.");
            if (Spectrum.HelloIntervalSeconds is < 1 or > 60)
                throw new InvalidDataException("Station spectrum HELLO interval must be between 1 and 60 seconds.");
            if (Spectrum.MaximumFramesPerSecond is < 1 or > 60)
                throw new InvalidDataException("Station spectrum frame rate must be between 1 and 60 frames per second.");
            if (Spectrum.Average is < 1 or > 10)
                throw new InvalidDataException("Station spectrum average must be between 1 and 10 frames.");
            if (Spectrum.MinimumLevel is < 0 or > 254)
                throw new InvalidDataException("Station spectrum minimum level must be between 0 and 254.");
            if (Spectrum.MaximumLevel is < 1 or > 255 || Spectrum.MaximumLevel <= Spectrum.MinimumLevel)
                throw new InvalidDataException("Station spectrum maximum level must be between 1 and 255 and greater than the minimum level.");
            if (Spectrum.WaterfallHeight is < 100 or > 800)
                throw new InvalidDataException("Station waterfall height must be between 100 and 800 pixels.");
            if (Spectrum.WaterfallRate is < 1 or > 60)
                throw new InvalidDataException("Station waterfall rate must be between 1 and 60 rows per second.");
        }
        foreach (StationFrequencyRange range in RemoteFrequencyPolicy.AllowedRanges)
        {
            if (range.MinimumHz <= 0 || range.MaximumHz <= range.MinimumHz)
                throw new InvalidDataException($"Remote frequency range '{range.Name}' must have positive limits with MaximumHz greater than MinimumHz.");
        }
    }
}

public sealed class StationSpectrum
{
    public bool Enabled { get; set; }
    public string SourceHost { get; set; } = "192.168.0.29";
    public int UdpPort { get; set; } = 51235;
    public int HelloIntervalSeconds { get; set; } = 2;
    public int MaximumFramesPerSecond { get; set; } = 30;
    public int Average { get; set; } = 4;
    public int MinimumLevel { get; set; } = 70;
    public int MaximumLevel { get; set; } = 150;
    public bool ShowWaterfall { get; set; } = true;
    public int WaterfallHeight { get; set; } = 260;
    public int WaterfallRate { get; set; } = 15;
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

public sealed class StationRemoteFrequencyPolicy
{
    public List<StationFrequencyRange> AllowedRanges { get; set; } = [];
}

public sealed class StationFrequencyRange
{
    public string Name { get; set; } = "Allowed range";
    public long MinimumHz { get; set; }
    public long MaximumHz { get; set; }
}
