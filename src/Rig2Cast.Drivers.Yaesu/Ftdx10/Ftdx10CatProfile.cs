using Rig2Cast.Abstractions.Radios;

namespace Rig2Cast.Drivers.Yaesu.Ftdx10;

public static class Ftdx10CatProfile
{
    public const string ModelId = "yaesu.ftdx10";
    public const string Identification = "0761";

    public static readonly IReadOnlyList<int> SupportedBaudRates =
        Array.AsReadOnly<int>([4_800, 9_600, 19_200, 38_400]);

    public static readonly IReadOnlyDictionary<char, RadioMode> Modes = new Dictionary<char, RadioMode>
    {
        ['1'] = RadioMode.Lsb,
        ['2'] = RadioMode.Usb,
        ['3'] = RadioMode.Cw,
        ['4'] = RadioMode.Fm,
        ['5'] = RadioMode.Am,
        ['6'] = RadioMode.Rtty,
        ['7'] = RadioMode.CwReverse,
        ['8'] = RadioMode.DataLsb,
        ['9'] = RadioMode.RttyReverse,
        ['A'] = RadioMode.DataFm,
        ['B'] = RadioMode.FmNarrow,
        ['C'] = RadioMode.DataUsb,
        ['D'] = RadioMode.AmNarrow,
        ['E'] = RadioMode.Psk,
        ['F'] = RadioMode.DataFmNarrow
    };

    public static char EncodeMode(RadioMode mode)
    {
        foreach ((char code, RadioMode candidate) in Modes)
        {
            if (candidate == mode)
            {
                return code;
            }
        }

        throw new NotSupportedException($"Operating mode '{mode}' is not supported by the FTDX10 CAT profile.");
    }
}
