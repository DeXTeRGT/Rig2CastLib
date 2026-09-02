using Rig2Cast.Abstractions.Radios;

namespace Rig2Cast.Drivers.Elecraft.K3Family;

public sealed record ElecraftK3Profile(string ModelId, string Model, bool SupportsFm)
{
    public const string K3SModelId = "elecraft.k3s";
    public const string K3ModelId = "elecraft.k3";
    public const string KX3ModelId = "elecraft.kx3";
    public const string KX2ModelId = "elecraft.kx2";

    public static readonly IReadOnlyList<int> SupportedBaudRates =
        Array.AsReadOnly<int>([4_800, 9_600, 19_200, 38_400]);

    public static readonly IReadOnlyList<int> AutoInformationModes =
        Array.AsReadOnly<int>([0, 1, 2, 3]);

    public static readonly IReadOnlyDictionary<string, ElecraftK3Profile> Models =
        new Dictionary<string, ElecraftK3Profile>(StringComparer.OrdinalIgnoreCase)
        {
            [K3SModelId] = new(K3SModelId, "K3S", true),
            [K3ModelId] = new(K3ModelId, "K3", true),
            [KX3ModelId] = new(KX3ModelId, "KX3", true),
            [KX2ModelId] = new(KX2ModelId, "KX2", false)
        };

    public static readonly IReadOnlyDictionary<char, RadioMode> Modes = new Dictionary<char, RadioMode>
    {
        ['1'] = RadioMode.Lsb,
        ['2'] = RadioMode.Usb,
        ['3'] = RadioMode.Cw,
        ['4'] = RadioMode.Fm,
        ['5'] = RadioMode.Am,
        ['6'] = RadioMode.DataUsb,
        ['7'] = RadioMode.CwReverse,
        ['9'] = RadioMode.DataLsb
    };

    // Modes must be a bijection: ToDictionary throws at class load if two wire codes ever
    // mapped to the same RadioMode, instead of EncodeMode failing ambiguously mid-command.
    private static readonly Dictionary<RadioMode, char> ReverseModes =
        Modes.ToDictionary(pair => pair.Value, pair => pair.Key);

    public IReadOnlySet<RadioMode> SupportedModes => SupportsFm
        ? Modes.Values.ToHashSet()
        : Modes.Values.Where(mode => mode != RadioMode.Fm).ToHashSet();

    public char EncodeMode(RadioMode mode)
    {
        if (!SupportedModes.Contains(mode))
            throw new NotSupportedException($"Operating mode '{mode}' is not supported by the {Model} profile.");
        return ReverseModes[mode];
    }

    public bool MatchesOptionResponse(string response)
    {
        if (!response.StartsWith("OM", StringComparison.OrdinalIgnoreCase) || !response.EndsWith(';'))
            return false;
        string payload = response[2..^1];
        return ModelId switch
        {
            K3SModelId => payload.Contains('R'),
            K3ModelId => !payload.Contains('R') && !IsPortable(payload),
            KX2ModelId => payload.EndsWith("01", StringComparison.Ordinal),
            KX3ModelId => payload.EndsWith("02", StringComparison.Ordinal),
            _ => false
        };
    }

    private static bool IsPortable(string payload) =>
        payload.EndsWith("01", StringComparison.Ordinal) || payload.EndsWith("02", StringComparison.Ordinal);
}
