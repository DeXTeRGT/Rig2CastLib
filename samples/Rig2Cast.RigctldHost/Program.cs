using System.Net;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Security;
using Rig2Cast.Adapters.Rigctld;
using Rig2Cast.Core.Drivers;
using Rig2Cast.Drivers.Yaesu.Ftdx10;
using Rig2Cast.Runtime.Sessions;
using Rig2Cast.Simulator;
using Rig2Cast.Transports.Serial;

bool simulator = HasFlag("--simulator");
bool allowWrite = HasFlag("--allow-write");
bool automaticInformation = HasFlag("--auto-information");
var catalog = new RadioDriverCatalog();
catalog.Register(new Ftdx10DriverFactory());

if (HasFlag("--list-models"))
{
    Console.WriteLine("MODEL ID       MANUFACTURER  MODEL   TRANSPORTS        BAUD RATES");
    foreach (RadioModelRegistration item in catalog.Models)
    {
        string transports = string.Join(',', item.Model.SupportedTransports);
        string baudRates = item.Model.SupportedBaudRates.Count == 0 ? "n/a" : string.Join(',', item.Model.SupportedBaudRates);
        Console.WriteLine($"{item.Model.Id,-14} {item.Model.Manufacturer,-13} {item.Model.Model,-7} {transports,-17} {baudRates}");
    }
    return;
}

string modelId = GetOption("--model") ?? Ftdx10CatProfile.ModelId;
RadioModelRegistration selectedModel = catalog.Find(modelId);
string serialPort = GetOption("--serial-port") ?? "COM11";
int baud = int.TryParse(GetOption("--baud"), out int parsedBaud)
    ? parsedBaud
    : selectedModel.Model.DefaultBaudRate ?? 38_400;
int tcpPort = int.TryParse(GetOption("--tcp-port"), out int parsedTcpPort) ? parsedTcpPort : 4532;
int maxClients = int.TryParse(GetOption("--max-clients"), out int parsedClients) ? parsedClients : 32;
IPAddress address = HasFlag("--listen-any") ? IPAddress.Any : IPAddress.Loopback;

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stopping.Cancel(); };

ManagedRadio managedRadio;
if (simulator)
{
    EnsureTransportSupported(selectedModel.Model, RadioTransportKind.Simulator);
    IRadioDriver driver = selectedModel.Model.Id.Equals(Ftdx10CatProfile.ModelId, StringComparison.OrdinalIgnoreCase)
        ? new SimulatedFtdx10Driver()
        : throw new NotSupportedException($"No simulator is registered for '{selectedModel.Model.Id}'.");
    managedRadio = await ManagedRadio.CreateAsync("radio-1", driver, cancellationToken: stopping.Token);
}
else
{
    EnsureTransportSupported(selectedModel.Model, RadioTransportKind.Serial);
    if (selectedModel.Model.SupportedBaudRates.Count > 0 && !selectedModel.Model.SupportedBaudRates.Contains(baud))
        throw new ArgumentException($"Baud rate {baud} is not supported by {selectedModel.Model.Id}. Supported values: {string.Join(", ", selectedModel.Model.SupportedBaudRates)}.");

    managedRadio = await ManagedRadio.CreateReconnectableAsync(
        "radio-1",
        async cancellationToken =>
        {
            var transport = new SerialRadioTransport(new SerialRadioTransportOptions
            {
                PortName = serialPort,
                BaudRate = baud,
                StopBits = System.IO.Ports.StopBits.Two,
                Handshake = System.IO.Ports.Handshake.RequestToSend
            });
            return await selectedModel.Factory.OpenAsync(
                new RadioConnectionOptions(
                    "radio-1",
                    selectedModel.Model.Id,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["serial-port"] = serialPort,
                        ["baud"] = baud.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["yaesu.autoInformation"] = automaticInformation.ToString()
                    }),
                transport,
                cancellationToken);
        },
        cancellationToken: stopping.Token);
}

await using ManagedRadio radio = managedRadio;
ClientRole role = allowWrite ? ClientRole.Controller : ClientRole.Observer;
await using var server = new RigctldServer(
    new RigctldServerOptions
    {
        Address = address,
        Port = tcpPort,
        MaximumClients = maxClients,
        WritesEnabled = allowWrite
    },
    clientId => radio.OpenSession(new ClientIdentity(clientId, "rigctld TCP client"), role));

server.Start();
Console.WriteLine($"Rig2Cast rigctld adapter listening on {server.LocalEndpoint}.");
Console.WriteLine($"Radio: {selectedModel.Model.Manufacturer} {selectedModel.Model.Model} ({selectedModel.Model.Id})" +
                  (simulator ? " using simulator" : $" on {serialPort} at {baud} baud"));
Console.WriteLine($"Access: {(allowWrite ? "writes enabled (PTT still disabled)" : "read-only")}; maximum clients: {maxClients}.");
Console.WriteLine("Press Ctrl+C to stop.");
try { await Task.Delay(Timeout.InfiniteTimeSpan, stopping.Token); }
catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }

bool HasFlag(string option) => args.Any(value => value.Equals(option, StringComparison.OrdinalIgnoreCase));
string? GetOption(string option)
{
    int index = Array.FindIndex(args, value => value.Equals(option, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static void EnsureTransportSupported(RadioModelDescriptor model, RadioTransportKind transport)
{
    if (!model.SupportedTransports.Contains(transport))
        throw new NotSupportedException($"Model '{model.Id}' does not support the {transport} transport.");
}
