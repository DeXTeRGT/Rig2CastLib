using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Protocols.Declarative;

namespace Rig2Cast.Drivers.Xiegu.G90;

public static class G90Profile
{
    public const string ModelId = "xiegu.g90";
    public const byte DefaultRadioAddress = 0x70;
    public const byte DefaultControllerAddress = 0xE0;
    public static IReadOnlyList<int> SupportedBaudRates { get; } = [19_200];

    public static ValueMapDescriptor<byte, RadioMode> ModeMap { get; } = new(
        "Xiegu G90 operating modes",
        new Dictionary<byte, RadioMode>
        {
            [0x00] = RadioMode.Lsb,
            [0x01] = RadioMode.Usb,
            [0x02] = RadioMode.Am,
            [0x03] = RadioMode.Cw,
            [0x05] = RadioMode.FmNarrow,
            [0x07] = RadioMode.CwReverse
        });
}
