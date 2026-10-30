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
    public StationUserManagement UserManagement { get; set; } = new();
    public StationPresentation Presentation { get; set; } = new();
    public List<StationRotator> Rotators { get; set; } = [];
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
        if (UserManagement.Enabled)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(UserManagement.DatabasePath);
            if (UserManagement.SessionHours is < 1 or > 720)
                throw new InvalidDataException("UserManagement.SessionHours must be between 1 and 720.");
        }
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
        if (Presentation.ShowRotatorGui)
        {
            if (!Rotators.Any(rotator => rotator.Enabled))
                throw new InvalidDataException("Presentation.ShowRotatorGui requires at least one enabled rotator.");
            foreach (StationRotator rotator in Rotators)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(rotator.Id);
                ArgumentException.ThrowIfNullOrWhiteSpace(rotator.ModelId);
                if (!rotator.Transport.Equals("Serial", StringComparison.OrdinalIgnoreCase) &&
                    !rotator.Transport.Equals("Tcp", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Rotator '{rotator.Id}' transport must be Serial or Tcp.");
                if (rotator.PollIntervalMilliseconds is < 100 or > 60000)
                    throw new InvalidDataException($"Rotator '{rotator.Id}' polling interval must be between 100 and 60000 ms.");
                if (rotator.CommandSettleMilliseconds is < 0 or > 5000)
                    throw new InvalidDataException($"Rotator '{rotator.Id}' command settle delay must be between 0 and 5000 ms.");
                if (!System.Text.RegularExpressions.Regex.IsMatch(rotator.BezelColor, "^#[0-9a-fA-F]{6}$"))
                    throw new InvalidDataException($"Rotator '{rotator.Id}' BezelColor must be a six-digit hexadecimal color such as #36cfe0.");
                if (rotator.MapOpacity is < 0 or > 1)
                    throw new InvalidDataException($"Rotator '{rotator.Id}' MapOpacity must be between 0 and 1.");
                if (rotator.BeamWidthDegrees is <= 0 or > 360)
                    throw new InvalidDataException($"Rotator '{rotator.Id}' BeamWidthDegrees must be greater than 0 and no more than 360.");
                if (!System.Text.RegularExpressions.Regex.IsMatch(rotator.BeamColor, "^#[0-9a-fA-F]{6}$"))
                    throw new InvalidDataException($"Rotator '{rotator.Id}' BeamColor must be a six-digit hexadecimal color such as #ffb548.");
                if (rotator.BeamOpacity is < 0 or > 1)
                    throw new InvalidDataException($"Rotator '{rotator.Id}' BeamOpacity must be between 0 and 1.");
                if (rotator.Transport.Equals("Tcp", StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(rotator.TcpHost) || rotator.TcpPort is < 1 or > 65535))
                    throw new InvalidDataException($"Rotator '{rotator.Id}' requires a valid TcpHost and TcpPort.");
                if (rotator.Transport.Equals("Serial", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(rotator.SerialPort))
                    throw new InvalidDataException($"Rotator '{rotator.Id}' requires SerialPort.");
            }
            if (Rotators.Select(rotator => rotator.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Rotators.Count)
                throw new InvalidDataException("Rotator IDs must be unique.");
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
    public bool AllowPowerControl { get; set; }
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
    public bool AllowRotatorControl { get; set; } = true;
}

public sealed class StationUserManagement
{
    public bool Enabled { get; set; }
    public string DatabasePath { get; set; } = "rig2cast-users.db";
    public int SessionHours { get; set; } = 12;
    public string? BootstrapAdminUsername { get; set; }

    public string ResolveDatabasePath(string contentRoot) =>
        Path.GetFullPath(Path.IsPathRooted(DatabasePath)
            ? DatabasePath
            : Path.Combine(contentRoot, DatabasePath));
}

public sealed class StationPresentation
{
    public bool ShowRotatorGui { get; set; }
    public List<string> Modes { get; set; } = ["Lsb", "Usb", "Cw", "CwReverse"];
    public List<string> Choices { get; set; } = ["Band", "TuningStep", "Attenuator", "Preamp", "Agc", "FilterWidth"];
    public List<string> Controls { get; set; } = ["AfGain", "RfGain", "TransmitPower", "IfShiftHz"];
    public List<string> Switches { get; set; } = ["NoiseBlanker", "NoiseReduction", "AutoNotch"];
    public List<string> Actions { get; set; } = ["CopyVfoAToB", "CopyVfoBToA", "SwapVfos"];
    public List<string> Meters { get; set; } = ["SignalStrength", "Power", "SWR"];
}

public sealed class StationRotator
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "Rotator";
    public bool Enabled { get; set; } = true;
    public bool AutoConnect { get; set; } = true;
    public bool AllowControl { get; set; } = true;
    public string ModelId { get; set; } = "yaesu.gs232";
    public string Transport { get; set; } = "Serial";
    public string? SerialPort { get; set; }
    public int BaudRate { get; set; } = 9600;
    public string? TcpHost { get; set; }
    public int? TcpPort { get; set; }
    public int PollIntervalMilliseconds { get; set; } = 500;
    public int CommandSettleMilliseconds { get; set; } = 100;
    public int ResponseTimeoutMilliseconds { get; set; } = 1500;
    public double? ParkAzimuth { get; set; }
    public string? MapImage { get; set; }
    public double MapNorthOffsetDegrees { get; set; }
    public double MapOpacity { get; set; } = 0.65;
    public string BezelColor { get; set; } = "#36cfe0";
    public bool ShowBeam { get; set; }
    public double BeamWidthDegrees { get; set; } = 60;
    public string BeamColor { get; set; } = "#ffb548";
    public double BeamOpacity { get; set; } = 0.28;
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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
