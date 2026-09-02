using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Events;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Security;
using Rig2Cast.Abstractions.Sessions;
using Rig2Cast.Core.Drivers;
using Rig2Cast.Drivers.Elecraft.K3Family;
using Rig2Cast.Drivers.Yaesu.Ftdx10;
using Rig2Cast.Runtime.Sessions;
using Rig2Cast.Simulator;
using Rig2Cast.Transports.Serial;
using System.Globalization;

bool simulator = HasFlag(args, "--simulator");
bool allowWrite = HasFlag(args, "--allow-write");
string? configuredAutoInformationMode = GetOption(args, "--auto-information-mode");
bool automaticInformation = HasFlag(args, "--auto-information") || configuredAutoInformationMode is not null;
int automaticInformationMode = int.TryParse(configuredAutoInformationMode, out int parsedAutoInformationMode)
    ? parsedAutoInformationMode : 1;
var catalog = new RadioDriverCatalog();
catalog.Register(new Ftdx10DriverFactory());
catalog.Register(new ElecraftK3DriverFactory());
if (HasFlag(args, "--list-models"))
{
    foreach (RadioModelRegistration item in catalog.Models)
        Console.WriteLine($"{item.Model.Id,-18} {item.Model.Manufacturer} {item.Model.Model} (default {item.Model.DefaultBaudRate} baud)");
    return;
}
string modelId = GetOption(args, "--model") ?? Ftdx10CatProfile.ModelId;
RadioModelRegistration selectedModel = catalog.Find(modelId);
string port = GetOption(args, "--port") ?? "COM11";
int baud = int.TryParse(GetOption(args, "--baud"), out int parsedBaud)
    ? parsedBaud
    : selectedModel.Model.DefaultBaudRate ?? 38_400;

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stopping.Cancel();
};

ManagedRadio managedRadio;
if (simulator)
{
    IRadioDriver driver = selectedModel.Model.Id.Equals(Ftdx10CatProfile.ModelId, StringComparison.OrdinalIgnoreCase)
        ? new SimulatedFtdx10Driver()
        : throw new NotSupportedException($"No sample simulator is registered for '{selectedModel.Model.Id}'.");
    Console.WriteLine($"Opening {selectedModel.Model.Model} simulator...");
    managedRadio = await ManagedRadio.CreateAsync("radio-1", driver, cancellationToken: stopping.Token);
}
else
{
    if (!selectedModel.Model.SupportedBaudRates.Contains(baud))
        throw new ArgumentException($"Baud rate {baud} is not supported by {modelId}. Supported values: {string.Join(", ", selectedModel.Model.SupportedBaudRates)}.");
    Console.WriteLine($"Opening {selectedModel.Model.Manufacturer} {selectedModel.Model.Model} on {port} at {baud} baud...");
    managedRadio = await ManagedRadio.CreateReconnectableAsync(
        "radio-1",
        async cancellationToken =>
        {
            var transport = new SerialRadioTransport(CreateSerialOptions(selectedModel.Model, port, baud));
            return await selectedModel.Factory.OpenAsync(
                new RadioConnectionOptions(
                    "radio-1",
                    modelId,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["yaesu.autoInformation"] = automaticInformation.ToString(),
                        ["elecraft.autoInformation"] = automaticInformation.ToString(),
                        ["elecraft.autoInformationMode"] = automaticInformationMode.ToString(CultureInfo.InvariantCulture)
                    }),
                transport,
                cancellationToken);
        },
        cancellationToken: stopping.Token);
}

await using ManagedRadio radio = managedRadio;
ClientRole role = allowWrite ? ClientRole.Operator : ClientRole.Observer;
await using IRadioSession session = radio.OpenSession(
    new ClientIdentity("console", "Rig2Cast diagnostic console"), role);
await using var transmitController = new RenewingTransmitController(session);

Console.WriteLine($"Connected in {(allowWrite ? "WRITE-ENABLED" : "READ-ONLY")} mode.");
Console.WriteLine("Type 'help' for commands. CAT PTT uses a time-limited transmit lease; tuner-start is unavailable.");

CancellationTokenSource? watchStopping = null;
Task? watchTask = null;
CancellationTokenSource? pollStopping = null;
Task? pollTask = null;

while (!stopping.IsCancellationRequested)
{
    Console.Write("rig2cast> ");
    string? line = Console.ReadLine();
    if (line is null)
    {
        break;
    }

    string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length == 0)
    {
        continue;
    }

    try
    {
        string command = parts[0].ToLowerInvariant();
        if (command is "exit" or "quit")
        {
            break;
        }

        switch (command)
        {
            case "help":
                PrintHelp();
                break;
            case "radio":
                PrintRadio((await session.GetSnapshotAsync()).Capabilities, allowWrite, simulator, port, baud);
                break;
            case "state":
            case "refresh":
                PrintState(await session.RefreshStateAsync(stopping.Token));
                break;
            case "capabilities":
            case "caps":
                PrintCapabilities((await session.GetSnapshotAsync()).Capabilities, parts.ElementAtOrDefault(1));
                break;
            case "meters":
                await PrintMetersAsync(session, parts.ElementAtOrDefault(1));
                break;
            case "passband":
                RadioPassbandValue passband = parts.Length <= 1
                    ? await session.ReadPassbandAsync(stopping.Token)
                    : IsReceiver(parts[1])
                        ? await session.ReadPassbandAsync(ParseReceiver(parts[1]), stopping.Token)
                        : await session.ReadPassbandAsync(ParseEnum<VfoId>(parts[1]), stopping.Token);
                Console.WriteLine($"Passband{FormatAddress(passband.Target, passband.Receiver)} = {passband.WidthHz} Hz");
                break;
            case "get":
                await ExecuteGetAsync(session, parts);
                break;
            case "set":
                EnsureWriteAllowed(allowWrite);
                await ExecuteSetAsync(session, parts);
                break;
            case "ptt":
                await ExecutePttAsync(transmitController, parts, allowWrite, stopping.Token);
                break;
            case "watch":
                (watchStopping, watchTask) = await ConfigureWatchAsync(session, parts, watchStopping, watchTask, stopping.Token);
                break;
            case "poll":
                (pollStopping, pollTask) = await ConfigurePollAsync(session, parts, pollStopping, pollTask, stopping.Token);
                break;
            default:
                Console.WriteLine($"Unknown command '{parts[0]}'. Type 'help'.");
                break;
        }
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        Console.WriteLine($"ERROR: {exception.Message}");
    }
}

if (watchStopping is not null)
{
    await watchStopping.CancelAsync();
    if (watchTask is not null)
    {
        await IgnoreCancellationAsync(watchTask);
    }
    watchStopping.Dispose();
}
if (pollStopping is not null)
{
    await pollStopping.CancelAsync();
    if (pollTask is not null) await IgnoreCancellationAsync(pollTask);
    pollStopping.Dispose();
}
static async Task ExecutePttAsync(
    RenewingTransmitController controller,
    string[] parts,
    bool allowWrite,
    CancellationToken cancellationToken)
{
    string action = parts.ElementAtOrDefault(1)?.ToLowerInvariant() ?? "status";
    if (action == "status")
    {
        RadioState state = await controller.GetStatusAsync(cancellationToken);
        Console.WriteLine($"PTT = {(state.IsTransmitting ? "on" : "off")} (hardware state)");
        if (controller.RenewalFailure is Exception failure)
            Console.WriteLine($"PTT lease renewal failed: {failure.Message}");
        return;
    }

    EnsureWriteAllowed(allowWrite);
    if (action == "on")
    {
        RadioState state;
        if (parts.Length > 2)
        {
            int seconds = int.Parse(parts[2], CultureInfo.InvariantCulture);
            if (seconds is < 1 or > 60)
                throw new ArgumentOutOfRangeException(nameof(parts), "PTT duration must be between 1 and 60 seconds.");
            state = await controller.StartForAsync(TimeSpan.FromSeconds(seconds), cancellationToken);
            Console.WriteLine($"PTT = {(state.IsTransmitting ? "on" : "off")}; automatic RX after at most {seconds} seconds");
        }
        else
        {
            state = await controller.StartContinuousAsync(cancellationToken);
            Console.WriteLine($"PTT = {(state.IsTransmitting ? "on" : "off")}; lease heartbeat active until 'ptt off'");
        }
        return;
    }

    if (action == "off")
    {
        RadioState state = await controller.StopAsync(cancellationToken);
        Console.WriteLine($"PTT = {(state.IsTransmitting ? "on" : "off")} (hardware state)");
        return;
    }

    throw new ArgumentException("Usage: ptt [status|on [seconds]|off]");
}

static async Task ExecuteGetAsync(IRadioSession session, string[] parts)
{
    RequireParts(parts, 3, "get <numeric|switch|choice> <name>");
    switch (parts[1].ToLowerInvariant())
    {
        case "numeric":
            RadioControlId numeric = ParseEnum<RadioControlId>(parts[2]);
            RadioControlValue numericValue = parts.Length > 3
                ? IsReceiver(parts[3])
                    ? await session.ReadControlAsync(numeric, ParseReceiver(parts[3]))
                    : await session.ReadControlAsync(numeric, ParseEnum<VfoId>(parts[3]))
                : await session.ReadControlAsync(numeric);
            Console.WriteLine($"{numeric}{FormatAddress(numericValue.Target, numericValue.Receiver)} = {numericValue.Value}");
            break;
        case "switch":
            RadioSwitchId switchId = ParseEnum<RadioSwitchId>(parts[2]);
            RadioSwitchValue switchValue = parts.Length > 3
                ? await session.ReadSwitchAsync(switchId, ParseReceiver(parts[3]))
                : await session.ReadSwitchAsync(switchId);
            Console.WriteLine($"{switchId}{FormatAddress(null, switchValue.Receiver)} = {(switchValue.Enabled ? "on" : "off")}");
            break;
        case "choice":
            RadioChoiceId choice = ParseEnum<RadioChoiceId>(parts[2]);
            RadioChoiceValue choiceValue = parts.Length > 3
                ? IsReceiver(parts[3])
                    ? await session.ReadChoiceAsync(choice, ParseReceiver(parts[3]))
                    : await session.ReadChoiceAsync(choice, ParseEnum<VfoId>(parts[3]))
                : await session.ReadChoiceAsync(choice);
            Console.WriteLine($"{choice}{FormatAddress(choiceValue.Target, choiceValue.Receiver)} = {choiceValue.Value}");
            break;
        default:
            throw new ArgumentException("Expected numeric, switch, or choice.");
    }
}

static async Task ExecuteSetAsync(IRadioSession session, string[] parts)
{
    RequireParts(parts, 3, "set <frequency|vfo|mode|split|numeric|switch|choice> ...");
    switch (parts[1].ToLowerInvariant())
    {
        case "frequency":
            RequireParts(parts, 4, "set frequency <main|sub|A|B> <hz>");
            long frequency = long.Parse(parts[3], CultureInfo.InvariantCulture);
            if (IsReceiver(parts[2]))
            {
                ReceiverId receiver = ParseReceiver(parts[2]);
                await session.SetFrequencyAsync(receiver, frequency);
                Console.WriteLine($"Confirmed receiver {receiver} = {(await session.GetSnapshotAsync()).State.Receivers[receiver].FrequencyHz} Hz");
            }
            else
            {
                VfoId target = ParseEnum<VfoId>(parts[2]);
                await session.SetFrequencyAsync(target, frequency);
                Console.WriteLine($"Confirmed {target} = {(await session.GetSnapshotAsync()).State.FrequenciesHz[target]} Hz");
            }
            break;
        case "vfo":
            VfoId vfo = ParseEnum<VfoId>(parts[2]);
            await session.SetActiveVfoAsync(vfo);
            Console.WriteLine($"Confirmed active VFO = {(await session.GetSnapshotAsync()).State.ActiveVfo}");
            break;
        case "mode":
            ReceiverId? modeReceiver = parts.Length > 3 ? ParseReceiver(parts[2]) : null;
            RadioMode mode = ParseEnum<RadioMode>(parts[modeReceiver is null ? 2 : 3]);
            if (modeReceiver is ReceiverId selectedModeReceiver)
                await session.SetModeAsync(selectedModeReceiver, mode);
            else
                await session.SetModeAsync(mode);
            RadioState modeState = (await session.GetSnapshotAsync()).State;
            Console.WriteLine(modeReceiver is ReceiverId confirmedModeReceiver
                ? $"Confirmed mode[{confirmedModeReceiver}] = {modeState.Receivers[confirmedModeReceiver].Mode}"
                : $"Confirmed mode = {modeState.Mode}");
            break;
        case "passband":
            string? passbandTarget = parts.Length > 3 ? parts[2] : null;
            int widthHz = int.Parse(parts[passbandTarget is null ? 2 : 3], CultureInfo.InvariantCulture);
            if (passbandTarget is not null && IsReceiver(passbandTarget))
                await session.SetPassbandAsync(ParseReceiver(passbandTarget), widthHz);
            else if (passbandTarget is not null)
                await session.SetPassbandAsync(ParseEnum<VfoId>(passbandTarget), widthHz);
            else
                await session.SetPassbandAsync(widthHz);
            RadioPassbandValue confirmedPassband = passbandTarget is null
                ? await session.ReadPassbandAsync()
                : IsReceiver(passbandTarget)
                    ? await session.ReadPassbandAsync(ParseReceiver(passbandTarget))
                    : await session.ReadPassbandAsync(ParseEnum<VfoId>(passbandTarget));
            Console.WriteLine($"Confirmed passband{FormatAddress(confirmedPassband.Target, confirmedPassband.Receiver)} = {confirmedPassband.WidthHz} Hz");
            break;
        case "split":
            bool split = ParseBoolean(parts[2]);
            await session.SetSplitAsync(split);
            Console.WriteLine($"Confirmed split = {(await session.GetSnapshotAsync()).State.IsSplit}");
            break;
        case "numeric":
            RequireParts(parts, 4, "set numeric <name> <value>");
            RadioControlId numeric = ParseEnum<RadioControlId>(parts[2]);
            string? numericTarget = parts.Length > 4 ? parts[3] : null;
            int numericValue = int.Parse(parts[numericTarget is null ? 3 : 4], CultureInfo.InvariantCulture);
            if (numericTarget is not null && IsReceiver(numericTarget))
                await session.WriteControlAsync(numeric, ParseReceiver(numericTarget), numericValue);
            else if (numericTarget is not null)
                await session.WriteControlAsync(numeric, ParseEnum<VfoId>(numericTarget), numericValue);
            else
                await session.WriteControlAsync(numeric, numericValue);
            RadioControlValue confirmedNumeric = numericTarget is null
                ? await session.ReadControlAsync(numeric)
                : IsReceiver(numericTarget)
                    ? await session.ReadControlAsync(numeric, ParseReceiver(numericTarget))
                    : await session.ReadControlAsync(numeric, ParseEnum<VfoId>(numericTarget));
            Console.WriteLine($"Confirmed {numeric}{FormatAddress(confirmedNumeric.Target, confirmedNumeric.Receiver)} = {confirmedNumeric.Value}");
            break;
        case "switch":
            RequireParts(parts, 4, "set switch <name> <on|off>");
            RadioSwitchId switchId = ParseEnum<RadioSwitchId>(parts[2]);
            string? switchTarget = parts.Length > 4 ? parts[3] : null;
            bool enabled = ParseBoolean(parts[switchTarget is null ? 3 : 4]);
            if (switchTarget is not null)
                await session.WriteSwitchAsync(switchId, ParseReceiver(switchTarget), enabled);
            else
                await session.WriteSwitchAsync(switchId, enabled);
            RadioSwitchValue confirmedSwitch = switchTarget is null
                ? await session.ReadSwitchAsync(switchId)
                : await session.ReadSwitchAsync(switchId, ParseReceiver(switchTarget));
            Console.WriteLine($"Confirmed {switchId}{FormatAddress(null, confirmedSwitch.Receiver)} = {(confirmedSwitch.Enabled ? "on" : "off")}");
            break;
        case "choice":
            RequireParts(parts, 4, "set choice <name> <value>");
            RadioChoiceId choice = ParseEnum<RadioChoiceId>(parts[2]);
            string? choiceTarget = parts.Length > 4 ? parts[3] : null;
            string choiceValue = parts[choiceTarget is null ? 3 : 4];
            if (choiceTarget is not null && IsReceiver(choiceTarget))
                await session.WriteChoiceAsync(choice, ParseReceiver(choiceTarget), choiceValue);
            else if (choiceTarget is not null)
                await session.WriteChoiceAsync(choice, ParseEnum<VfoId>(choiceTarget), choiceValue);
            else
                await session.WriteChoiceAsync(choice, choiceValue);
            RadioChoiceValue confirmedChoice = choiceTarget is null
                ? await session.ReadChoiceAsync(choice)
                : IsReceiver(choiceTarget)
                    ? await session.ReadChoiceAsync(choice, ParseReceiver(choiceTarget))
                    : await session.ReadChoiceAsync(choice, ParseEnum<VfoId>(choiceTarget));
            Console.WriteLine($"Confirmed {choice}{FormatAddress(confirmedChoice.Target, confirmedChoice.Receiver)} = {confirmedChoice.Value}");
            break;
        default:
            throw new ArgumentException("Unknown setter category.");
    }
}

static void PrintRadio(RadioCapabilities capabilities, bool allowWrite, bool simulator, string port, int baud)
{
    Console.WriteLine($"Radio: {capabilities.Manufacturer} {capabilities.Model}");
    Console.WriteLine($"Driver: {capabilities.DriverId} {capabilities.DriverVersion}");
    Console.WriteLine($"Connection: {(simulator ? "simulator" : $"{port} at {baud} baud")}");
    Console.WriteLine($"Console access: {(allowWrite ? "write-enabled" : "read-only")}");
}

static void PrintState(RadioState state)
{
    Console.WriteLine($"Connection: {state.Connection}");
    Console.WriteLine($"Active VFO: {state.ActiveVfo}");
    Console.WriteLine($"Transmit VFO: {state.TransmitVfo}");
    Console.WriteLine($"Receive paths: {string.Join(", ", state.ReceivePaths.Select(FormatSignalPath))}");
    Console.WriteLine($"Transmit path: {(state.TransmitPath is RadioSignalPath path ? FormatSignalPath(path) : "unknown")}");
    foreach ((VfoId vfo, long frequency) in state.FrequenciesHz)
    {
        Console.WriteLine($"VFO {vfo}: {frequency} Hz");
    }
    Console.WriteLine($"Mode: {state.Mode}; Split: {state.IsSplit}; Transmitting: {state.IsTransmitting}");
}

static string FormatSignalPath(RadioSignalPath path) =>
    path.Vfo is VfoId vfo ? $"{path.Receiver} <- VFO {vfo}" : path.Receiver.ToString();

static void PrintCapabilities(RadioCapabilities capabilities, string? category)
{
    string selected = category?.ToLowerInvariant() ?? "all";
    if (selected is "all" or "core")
    {
        Console.WriteLine($"VFOs: {string.Join(", ", capabilities.Vfos.Available)}");
        Console.WriteLine($"Frequency targets: {string.Join(", ", capabilities.Frequency.Targets)}");
        Console.WriteLine($"Frequency receiver targets: {FormatReceiverTargets(capabilities.Frequency.ReceiverTargets)}");
        Console.WriteLine($"Modes: {string.Join(", ", capabilities.Modes.Values)}");
        Console.WriteLine($"Mode receiver targets: {FormatReceiverTargets(capabilities.Modes.ReceiverTargets)}");
        Console.WriteLine($"Receivers: {string.Join(", ", capabilities.Receivers.Available.Keys)}");
        Console.WriteLine($"PTT: {capabilities.Transmit.Support}, {capabilities.Transmit.Access}, lease={capabilities.Transmit.RequiredLease ?? "none"}");
        Console.WriteLine($"Passband: {capabilities.Passband.Feature.Support}, {capabilities.Passband.Feature.Access}, VFO targets={FormatTargets(capabilities.Passband.Targets)}, receiver targets={FormatReceiverTargets(capabilities.Passband.ReceiverTargets)}");
        foreach ((RadioMode mode, PassbandConstraint constraint) in capabilities.Passband.ByMode)
        {
            string values = constraint.DiscreteValuesHz is null
                ? $"{constraint.MinimumHz}..{constraint.MaximumHz} Hz, step {constraint.StepHz}, radioQuantizes={constraint.RadioMayQuantize}"
                : string.Join(",", constraint.DiscreteValuesHz) + " Hz";
            Console.WriteLine($"  {mode}: {values}");
        }
    }
    if (selected is "all" or "numeric")
    {
        Console.WriteLine("Numeric controls:");
        foreach (NumericControlDescriptor item in capabilities.Controls.Values)
            Console.WriteLine($"  {item.Id}: {item.Minimum}..{item.Maximum}, step {item.Step}, {item.Unit}, {item.Feature.Access}, VFO targets={FormatTargets(item.Targets)}, receiver targets={FormatReceiverTargets(item.ReceiverTargets)}");
    }
    if (selected is "all" or "switches" or "switch")
    {
        Console.WriteLine("Switches:");
        foreach (SwitchControlDescriptor item in capabilities.Switches.Values)
            Console.WriteLine($"  {item.Id}: {item.Feature.Access}, receiver targets={FormatReceiverTargets(item.ReceiverTargets)}");
    }
    if (selected is "all" or "choices" or "choice")
    {
        Console.WriteLine("Choices:");
        foreach (ChoiceControlDescriptor item in capabilities.Choices.Values)
        {
            Console.WriteLine($"  {item.Id}: VFO targets={FormatTargets(item.Targets)}, receiver targets={FormatReceiverTargets(item.ReceiverTargets)}");
            foreach (RadioChoiceOption option in item.Options.Values)
            {
                string modes = option.ApplicableModes is null ? "all" : string.Join(",", option.ApplicableModes);
                Console.WriteLine($"    {option.Value} ({option.DisplayName}), writable={option.Writable}, modes={modes}");
            }
        }
    }
    if (selected is "all" or "meters" or "meter")
    {
        Console.WriteLine("Meters:");
        foreach (RadioMeterDescriptor item in capabilities.Meters.Values)
            Console.WriteLine($"  {item.Id}: {item.RawMinimum}..{item.RawMaximum} {item.RawUnit}, calibrated={item.CalibrationAvailable}, requires TX={item.RequiresTransmit}, VFO targets={FormatTargets(item.RangesByTarget?.Keys)}, receiver targets={FormatReceiverTargets(item.RangesByReceiver?.Keys)}");
    }
}

static async Task PrintMetersAsync(IRadioSession session, string? target)
{
    RadioSnapshot snapshot = await session.GetSnapshotAsync();
    RadioCapabilities capabilities = snapshot.Capabilities;
    foreach (RadioMeterId meter in capabilities.Meters.Keys)
    {
        RadioMeterDescriptor descriptor = capabilities.Meters[meter];
        if (descriptor.RequiresTransmit && !snapshot.State.IsTransmitting)
        {
            Console.WriteLine($"{meter}: skipped (available while transmitting)");
            continue;
        }
        if (target is not null && IsReceiver(target) &&
            (descriptor.RangesByReceiver is null ||
             !descriptor.RangesByReceiver.ContainsKey(ParseReceiver(target))))
            continue;
        if (target is not null && !IsReceiver(target) &&
            (descriptor.RangesByTarget is null ||
             !descriptor.RangesByTarget.ContainsKey(ParseEnum<VfoId>(target))))
            continue;
        RadioMeterReading reading = target is null
            ? await session.ReadMeterAsync(meter)
            : IsReceiver(target)
                ? await session.ReadMeterAsync(meter, ParseReceiver(target))
                : await session.ReadMeterAsync(meter, ParseEnum<VfoId>(target));
        Console.WriteLine($"{meter}{FormatAddress(reading.Target, reading.Receiver)}: {reading.RawValue} ({reading.NormalizedValue:P1})");
    }
}

static async Task<(CancellationTokenSource?, Task?)> ConfigureWatchAsync(
    IRadioSession session, string[] parts, CancellationTokenSource? currentStopping, Task? currentTask, CancellationToken applicationStopping)
{
    string action = parts.ElementAtOrDefault(1)?.ToLowerInvariant() ?? "on";
    if (action == "off")
    {
        if (currentStopping is not null)
        {
            await currentStopping.CancelAsync();
            if (currentTask is not null) await IgnoreCancellationAsync(currentTask);
            currentStopping.Dispose();
        }
        Console.WriteLine("Event watch stopped.");
        return (null, null);
    }
    if (currentTask is not null)
    {
        Console.WriteLine("Event watch is already active.");
        return (currentStopping, currentTask);
    }
    var watchStopping = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping);
    Task watchTask = Task.Run(async () =>
    {
        await foreach (RadioEvent radioEvent in session.WatchEventsAsync(watchStopping.Token))
            Console.WriteLine($"\nEVENT #{radioEvent.Sequence}: {radioEvent.Kind} -> {FormatEventPayload(radioEvent.Payload)}");
    }, watchStopping.Token);
    Console.WriteLine("Event watch started. Use 'watch off' to stop.");
    return (watchStopping, watchTask);
}

static string FormatEventPayload(object? payload)
{
    if (payload is not RadioState state)
    {
        return payload?.ToString() ?? "(none)";
    }

    string frequencies = string.Join(", ", state.FrequenciesHz
        .OrderBy(pair => pair.Key)
        .Select(pair => $"{pair.Key}={pair.Value} Hz"));
    return $"Revision={state.Revision}, Connection={state.Connection}, {frequencies}, Active={state.ActiveVfo}, TXVFO={state.TransmitVfo}, Mode={state.Mode}, " +
        $"Split={state.IsSplit}, TX={state.IsTransmitting}, Observed={state.ObservedAt:O}";
}

static async Task<(CancellationTokenSource?, Task?)> ConfigurePollAsync(
    IRadioSession session, string[] parts, CancellationTokenSource? currentStopping, Task? currentTask, CancellationToken applicationStopping)
{
    string action = parts.ElementAtOrDefault(1)?.ToLowerInvariant() ?? "start";
    if (action is "stop" or "off")
    {
        if (currentStopping is not null)
        {
            await currentStopping.CancelAsync();
            if (currentTask is not null) await IgnoreCancellationAsync(currentTask);
            currentStopping.Dispose();
        }
        Console.WriteLine("State polling stopped.");
        return (null, null);
    }
    if (action is not ("start" or "on"))
        throw new ArgumentException("Usage: poll start [milliseconds] | poll stop");
    if (currentTask is not null)
    {
        Console.WriteLine("State polling is already active.");
        return (currentStopping, currentTask);
    }
    int milliseconds = parts.Length >= 3
        ? int.Parse(parts[2], CultureInfo.InvariantCulture)
        : 500;
    if (milliseconds is < 100 or > 60_000)
        throw new ArgumentException("Polling interval must be between 100 and 60000 ms.");

    var pollStopping = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping);
    Task pollTask = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(milliseconds));
        while (await timer.WaitForNextTickAsync(pollStopping.Token))
            await session.RefreshStateAsync(pollStopping.Token);
    }, pollStopping.Token);
    Console.WriteLine($"State polling started every {milliseconds} ms. Use 'watch on' to display changes.");
    return (pollStopping, pollTask);
}

static void PrintHelp()
{
    Console.WriteLine("Read: radio | state | refresh | capabilities [core|numeric|switches|choices|meters] | meters [main|sub|A|B] | passband [main|sub|A|B]");
    Console.WriteLine("Read: get numeric <name> [main|sub|A|B] | get switch <name> [main|sub] | get choice <name> [main|sub|A|B]");
    Console.WriteLine("Events: watch [on|off] | poll start [milliseconds] | poll stop");
    Console.WriteLine("Transmit: ptt status | ptt on (renewed until off) | ptt on <1..60 seconds> | ptt off (writes require --allow-write)");
    Console.WriteLine("Write: set frequency <main|sub|A|B> <hz> | set vfo <A|B> | set mode [main|sub] <mode> | set passband [main|sub|A|B] <hz> | set split <on|off>");
    Console.WriteLine("Write: set numeric <name> [main|sub|A|B] <value> | set switch <name> [main|sub] <on|off> | set choice <name> [main|sub|A|B] <value>");
    Console.WriteLine("Exit: exit | quit");
}

static string FormatTarget(VfoId? target) => target is null ? string.Empty : $"[{target}]";

static string FormatAddress(VfoId? target, ReceiverId? receiver) =>
    receiver is ReceiverId selectedReceiver ? $"[{selectedReceiver}]" : FormatTarget(target);

static string FormatTargets(IEnumerable<VfoId>? targets) =>
    targets is null || !targets.Any() ? "primary" : string.Join(",", targets);

static string FormatReceiverTargets(IEnumerable<ReceiverId>? targets) =>
    targets is null || !targets.Any() ? "none" : string.Join(",", targets);

static bool IsReceiver(string value) =>
    value.Equals("main", StringComparison.OrdinalIgnoreCase) ||
    value.Equals("sub", StringComparison.OrdinalIgnoreCase) ||
    value.StartsWith("receiver-", StringComparison.OrdinalIgnoreCase) ||
    value.StartsWith("slice-", StringComparison.OrdinalIgnoreCase);

static ReceiverId ParseReceiver(string value) =>
    string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException("Receiver identity cannot be empty.")
        : new ReceiverId(value.ToLowerInvariant());

static void EnsureWriteAllowed(bool allowed)
{
    if (!allowed) throw new UnauthorizedAccessException("Writes are disabled. Restart with --allow-write.");
}

static T ParseEnum<T>(string value) where T : struct, Enum =>
    Enum.TryParse(value, true, out T parsed) ? parsed : throw new ArgumentException($"Unknown {typeof(T).Name} '{value}'.");

static bool ParseBoolean(string value) => value.ToLowerInvariant() switch
{
    "on" or "true" or "1" => true,
    "off" or "false" or "0" => false,
    _ => throw new ArgumentException($"Expected on/off, received '{value}'.")
};

static void RequireParts(string[] parts, int count, string usage)
{
    if (parts.Length < count) throw new ArgumentException($"Usage: {usage}");
}

static bool HasFlag(string[] arguments, string option) =>
    arguments.Any(value => StringComparer.OrdinalIgnoreCase.Equals(value, option));

static string? GetOption(string[] arguments, string option)
{
    int index = Array.FindIndex(arguments, value => StringComparer.OrdinalIgnoreCase.Equals(value, option));
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}

static async Task IgnoreCancellationAsync(Task task)
{
    try { await task; }
    catch (OperationCanceledException) { }
}

static SerialRadioTransportOptions CreateSerialOptions(RadioModelDescriptor model, string port, int baud)
{
    IReadOnlyDictionary<string, string> settings = model.DefaultConnectionSettings ??
        new Dictionary<string, string>();
    return new SerialRadioTransportOptions
    {
        PortName = port,
        BaudRate = baud,
        DataBits = GetInt(settings, "serial.dataBits", 8),
        StopBits = GetEnum(settings, "serial.stopBits", System.IO.Ports.StopBits.One),
        Parity = GetEnum(settings, "serial.parity", System.IO.Ports.Parity.None),
        Handshake = GetEnum(settings, "serial.handshake", System.IO.Ports.Handshake.None),
        DtrEnable = GetBool(settings, "serial.dtrEnable"),
        RtsEnable = GetBool(settings, "serial.rtsEnable")
    };
}

static int GetInt(IReadOnlyDictionary<string, string> settings, string key, int fallback) =>
    settings.TryGetValue(key, out string? value) && int.TryParse(value, out int parsed) ? parsed : fallback;

static bool GetBool(IReadOnlyDictionary<string, string> settings, string key) =>
    settings.TryGetValue(key, out string? value) && bool.TryParse(value, out bool parsed) && parsed;

static T GetEnum<T>(IReadOnlyDictionary<string, string> settings, string key, T fallback) where T : struct, Enum =>
    settings.TryGetValue(key, out string? value) && Enum.TryParse(value, true, out T parsed) ? parsed : fallback;
