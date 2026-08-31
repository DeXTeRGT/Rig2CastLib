using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Drivers.Yaesu.Ftdx10;
using Rig2Cast.Transports.Serial;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Abstractions.Controls;

string portName = GetOption(args, "--port") ?? "COM11";
int baudRate = int.TryParse(GetOption(args, "--baud"), out int configuredBaudRate)
    ? configuredBaudRate
    : 38_400;
bool automaticInformation = args.Any(value =>
    StringComparer.OrdinalIgnoreCase.Equals(value, "--auto-information"));

var transport = new SerialRadioTransport(new SerialRadioTransportOptions
{
    PortName = portName,
    BaudRate = baudRate,
    StopBits = System.IO.Ports.StopBits.Two,
    Handshake = System.IO.Ports.Handshake.RequestToSend
});

Console.WriteLine($"Opening {portName} at {baudRate} baud for read-only FTDX10 validation...");
await using Ftdx10Driver driver = await Ftdx10Driver.OpenAsync(
    transport,
    enableAutomaticInformation: automaticInformation);
RadioState state = await driver.ReadStateAsync();

Console.WriteLine("FTDX10 identification verified: ID0761");
Console.WriteLine($"VFO A: {state.FrequenciesHz[VfoId.A]} Hz");
Console.WriteLine($"VFO B: {state.FrequenciesHz[VfoId.B]} Hz");
Console.WriteLine($"Active VFO: {state.ActiveVfo}");
Console.WriteLine($"Mode: {state.Mode}");
Console.WriteLine($"Split: {state.IsSplit}");
Console.WriteLine($"Transmitting: {state.IsTransmitting}");
Console.WriteLine("Raw meters:");
foreach (RadioMeterId meter in Enum.GetValues<RadioMeterId>())
{
    RadioMeterReading reading = await driver.ReadMeterAsync(meter);
    Console.WriteLine($"  {meter}: {reading.RawValue} ({reading.NormalizedValue:P1})");
}

Console.WriteLine("Switches:");
foreach (RadioSwitchId control in Enum.GetValues<RadioSwitchId>())
{
    RadioSwitchValue value = await driver.ReadSwitchAsync(control);
    Console.WriteLine($"  {control}: {value.Enabled}");
}

Console.WriteLine("Filtering controls:");
foreach (RadioControlId control in new[]
         {
             RadioControlId.IfShiftHz,
             RadioControlId.ManualNotchFrequencyHz,
             RadioControlId.ContourFrequencyHz,
             RadioControlId.ClarifierOffsetHz
         })
{
    RadioControlValue value = await driver.ReadControlAsync(control);
    Console.WriteLine($"  {control}: {value.Value} Hz");
}

Console.WriteLine("Choices:");
foreach (RadioChoiceId control in Enum.GetValues<RadioChoiceId>())
{
    RadioChoiceValue value = await driver.ReadChoiceAsync(control);
    Console.WriteLine($"  {control}: {value.Value}");
}

static string? GetOption(string[] arguments, string option)
{
    int index = Array.FindIndex(arguments, value => StringComparer.OrdinalIgnoreCase.Equals(value, option));
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}
