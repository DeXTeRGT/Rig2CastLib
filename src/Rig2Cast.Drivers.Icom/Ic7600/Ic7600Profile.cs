using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Protocols.Declarative;

namespace Rig2Cast.Drivers.Icom.Ic7600;

public static class Ic7600Profile
{
    public const string ModelId = "icom.ic-7600";
    public const byte ModelIdentity = 0x7A;
    public const byte DefaultRadioAddress = 0x7A;
    public const byte DefaultControllerAddress = 0xE0;

    public static IReadOnlyList<int> SupportedBaudRates { get; } =
        [300, 1_200, 4_800, 9_600, 19_200];

    public static ValueMapDescriptor<byte, RadioMode> ModeMap { get; } = new(
        "Icom IC-7600 initial operating modes",
        new Dictionary<byte, RadioMode>
        {
            [0x00] = RadioMode.Lsb,
            [0x01] = RadioMode.Usb,
            [0x02] = RadioMode.Am,
            [0x03] = RadioMode.Cw,
            [0x05] = RadioMode.Fm,
            [0x07] = RadioMode.CwReverse
        });
}
