using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Protocols.Declarative;

namespace Rig2Cast.Drivers.Icom.Ic7300;

public static class Ic7300Profile
{
    public const string ModelId = "icom.ic-7300";
    public const byte DefaultRadioAddress = 0x94;
    public const byte DefaultControllerAddress = 0xE0;

    public static IReadOnlyList<int> SupportedBaudRates { get; } =
        [4_800, 9_600, 19_200, 38_400, 57_600, 115_200];

    public static ValueMapDescriptor<byte, RadioMode> ModeMap { get; } = new(
        "Icom IC-7300 operating modes",
        new Dictionary<byte, RadioMode>
        {
            [0x00] = RadioMode.Lsb,
            [0x01] = RadioMode.Usb,
            [0x02] = RadioMode.Am,
            [0x03] = RadioMode.Cw,
            [0x04] = RadioMode.Rtty,
            [0x05] = RadioMode.Fm,
            [0x07] = RadioMode.CwReverse,
            [0x08] = RadioMode.RttyReverse
        });
}
