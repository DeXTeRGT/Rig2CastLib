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

Console.WriteLine($"Connected in {(allowWrite ? "WRITE-ENABLED" : "READ-ONLY")} mode.");
Console.WriteLine("Type 'help' for commands. PTT and tuner-start are not available in this console.");

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
                await PrintMetersAsync(session, parts.Length > 1 ? ParseEnum<VfoId>(parts[1]) : null);
                break;
            case "passband":
                RadioPassbandValue passband = parts.Length > 1
                    ? await session.ReadPassbandAsync(ParseEnum<VfoId>(parts[1]), stopping.Token)
                    : await session.ReadPassbandAsync(stopping.Token);
                Console.WriteLine($"Passband{FormatTarget(passband.Target)} = {passband.WidthHz} Hz");
                break;
            case "get":
                await ExecuteGetAsync(session, parts);
                break;
            case "set":
                EnsureWriteAllowed(allowWrite);
                await ExecuteSetAsync(session, parts);
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

static async Task ExecuteGetAsync(IRadioSession session, string[] parts)
{
    RequireParts(parts, 3, "get <numeric|switch|choice> <name>");
    switch (parts[1].ToLowerInvariant())
    {
        case "numeric":
            RadioControlId numeric = ParseEnum<RadioControlId>(parts[2]);
            RadioControlValue numericValue = parts.Length > 3
                ? await session.ReadControlAsync(numeric, ParseEnum<VfoId>(parts[3]))
                : await session.ReadControlAsync(numeric);
            Console.WriteLine($"{numeric}{FormatTarget(numericValue.Target)} = {numericValue.Value}");
            break;
        case "switch":
            RadioSwitchId switchId = ParseEnum<RadioSwitchId>(parts[2]);
            RadioSwitchValue switchValue = await session.ReadSwitchAsync(switchId);
            Console.WriteLine($"{switchId} = {(switchValue.Enabled ? "on" : "off")}");
            break;
        case "choice":
            RadioChoiceId choice = ParseEnum<RadioChoiceId>(parts[2]);
            RadioChoiceValue choiceValue = parts.Length > 3
                ? await session.ReadChoiceAsync(choice, ParseEnum<VfoId>(parts[3]))
                : await session.ReadChoiceAsync(choice);
            Console.WriteLine($"{choice}{FormatTarget(choiceValue.Target)} = {choiceValue.Value}");
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
            RequireParts(parts, 4, "set frequency <A|B> <hz>");
            VfoId target = ParseEnum<VfoId>(parts[2]);
            long frequency = long.Parse(parts[3], CultureInfo.InvariantCulture);
            await session.SetFrequencyAsync(target, frequency);
            Console.WriteLine($"Confirmed {target} = {(await session.GetSnapshotAsync()).State.FrequenciesHz[target]} Hz");
            break;
        case "vfo":
            VfoId vfo = ParseEnum<VfoId>(parts[2]);
            await session.SetActiveVfoAsync(vfo);
            Console.WriteLine($"Confirmed active VFO = {(await session.GetSnapshotAsync()).State.ActiveVfo}");
            break;
        case "mode":
            RadioMode mode = ParseEnum<RadioMode>(parts[2]);
            await session.SetModeAsync(mode);
            Console.WriteLine($"Confirmed mode = {(await session.GetSnapshotAsync()).State.Mode}");
            break;
        case "passband":
            VfoId? passbandTarget = parts.Length > 3 ? ParseEnum<VfoId>(parts[2]) : null;
            int widthHz = int.Parse(parts[passbandTarget is null ? 2 : 3], CultureInfo.InvariantCulture);
            if (passbandTarget is VfoId selectedPassbandTarget)
                await session.SetPassbandAsync(selectedPassbandTarget, widthHz);
            else
                await session.SetPassbandAsync(widthHz);
            RadioPassbandValue confirmedPassband = passbandTarget is VfoId confirmedTarget
                ? await session.ReadPassbandAsync(confirmedTarget)
                : await session.ReadPassbandAsync();
            Console.WriteLine($"Confirmed passband{FormatTarget(confirmedPassband.Target)} = {confirmedPassband.WidthHz} Hz");
            break;
        case "split":
            bool split = ParseBoolean(parts[2]);
            await session.SetSplitAsync(split);
            Console.WriteLine($"Confirmed split = {(await session.GetSnapshotAsync()).State.IsSplit}");
            break;
        case "numeric":
            RequireParts(parts, 4, "set numeric <name> <value>");
            RadioControlId numeric = ParseEnum<RadioControlId>(parts[2]);
            VfoId? numericTarget = parts.Length > 4 ? ParseEnum<VfoId>(parts[3]) : null;
            int numericValue = int.Parse(parts[numericTarget is null ? 3 : 4], CultureInfo.InvariantCulture);
            if (numericTarget is VfoId selectedNumericTarget)
                await session.WriteControlAsync(numeric, selectedNumericTarget, numericValue);
            else
                await session.WriteControlAsync(numeric, numericValue);
            RadioControlValue confirmedNumeric = numericTarget is VfoId confirmedNumericTarget
                ? await session.ReadControlAsync(numeric, confirmedNumericTarget)
                : await session.ReadControlAsync(numeric);
            Console.WriteLine($"Confirmed {numeric}{FormatTarget(confirmedNumeric.Target)} = {confirmedNumeric.Value}");
            break;
        case "switch":
            RequireParts(parts, 4, "set switch <name> <on|off>");
            RadioSwitchId switchId = ParseEnum<RadioSwitchId>(parts[2]);
            bool enabled = ParseBoolean(parts[3]);
            await session.WriteSwitchAsync(switchId, enabled);
            Console.WriteLine($"Confirmed {switchId} = {((await session.ReadSwitchAsync(switchId)).Enabled ? "on" : "off")}");
            break;
        case "choice":
            RequireParts(parts, 4, "set choice <name> <value>");
            RadioChoiceId choice = ParseEnum<RadioChoiceId>(parts[2]);
            VfoId? choiceTarget = parts.Length > 4 ? ParseEnum<VfoId>(parts[3]) : null;
            string choiceValue = parts[choiceTarget is null ? 3 : 4];
            if (choiceTarget is VfoId selectedChoiceTarget)
                await session.WriteChoiceAsync(choice, selectedChoiceTarget, choiceValue);
            else
                await session.WriteChoiceAsync(choice, choiceValue);
            RadioChoiceValue confirmedChoice = choiceTarget is VfoId confirmedChoiceTarget
                ? await session.ReadChoiceAsync(choice, confirmedChoiceTarget)
                : await session.ReadChoiceAsync(choice);
            Console.WriteLine($"Confirmed {choice}{FormatTarget(confirmedChoice.Target)} = {confirmedChoice.Value}");
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
    foreach ((VfoId vfo, long frequency) in state.FrequenciesHz)
    {
        Console.WriteLine($"VFO {vfo}: {frequency} Hz");
    }
    Console.WriteLine($"Mode: {state.Mode}; Split: {state.IsSplit}; Transmitting: {state.IsTransmitting}");
}

static void PrintCapabilities(RadioCapabilities capabilities, string? category)
{
    string selected = category?.ToLowerInvariant() ?? "all";
    if (selected is "all" or "core")
    {
        Console.WriteLine($"VFOs: {string.Join(", ", capabilities.Vfos.Available)}");
        Console.WriteLine($"Frequency targets: {string.Join(", ", capabilities.Frequency.Targets)}");
        Console.WriteLine($"Modes: {string.Join(", ", capabilities.Modes.Values)}");
        Console.WriteLine($"PTT: {capabilities.Transmit.Support}, {capabilities.Transmit.Access}, lease={capabilities.Transmit.RequiredLease ?? "none"}");
        Console.WriteLine($"Passband: {capabilities.Passband.Feature.Support}, {capabilities.Passband.Feature.Access}, targets={FormatTargets(capabilities.Passband.Targets)}");
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
            Console.WriteLine($"  {item.Id}: {item.Minimum}..{item.Maximum}, step {item.Step}, {item.Unit}, {item.Feature.Access}, targets={FormatTargets(item.Targets)}");
    }
    if (selected is "all" or "switches" or "switch")
    {
        Console.WriteLine("Switches:");
        foreach (SwitchControlDescriptor item in capabilities.Switches.Values)
            Console.WriteLine($"  {item.Id}: {item.Feature.Access}");
    }
    if (selected is "all" or "choices" or "choice")
    {
        Console.WriteLine("Choices:");
        foreach (ChoiceControlDescriptor item in capabilities.Choices.Values)
        {
            Console.WriteLine($"  {item.Id}: targets={FormatTargets(item.Targets)}");
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
            Console.WriteLine($"  {item.Id}: {item.RawMinimum}..{item.RawMaximum} {item.RawUnit}, calibrated={item.CalibrationAvailable}, targets={FormatTargets(item.RangesByTarget?.Keys)}");
    }
}

static async Task PrintMetersAsync(IRadioSession session, VfoId? target)
{
    RadioCapabilities capabilities = (await session.GetSnapshotAsync()).Capabilities;
    foreach (RadioMeterId meter in capabilities.Meters.Keys)
    {
        if (target is VfoId selectedTarget &&
            (capabilities.Meters[meter].RangesByTarget is null ||
             !capabilities.Meters[meter].RangesByTarget!.ContainsKey(selectedTarget)))
            continue;
        RadioMeterReading reading = target is VfoId requestedTarget
            ? await session.ReadMeterAsync(meter, requestedTarget)
            : await session.ReadMeterAsync(meter);
        Console.WriteLine($"{meter}{FormatTarget(reading.Target)}: {reading.RawValue} ({reading.NormalizedValue:P1})");
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
    Console.WriteLine("Read: radio | state | refresh | capabilities [core|numeric|switches|choices|meters] | meters [A|B] | passband [A|B]");
    Console.WriteLine("Read: get numeric <name> [A|B] | get switch <name> | get choice <name> [A|B]");
    Console.WriteLine("Events: watch [on|off] | poll start [milliseconds] | poll stop");
    Console.WriteLine("Write: set frequency <A|B> <hz> | set vfo <A|B> | set mode <mode> | set passband [A|B] <hz> | set split <on|off>");
    Console.WriteLine("Write: set numeric <name> [A|B] <value> | set switch <name> <on|off> | set choice <name> [A|B] <value>");
    Console.WriteLine("Exit: exit | quit");
}

static string FormatTarget(VfoId? target) => target is null ? string.Empty : $"[{target}]";

static string FormatTargets(IEnumerable<VfoId>? targets) =>
    targets is null || !targets.Any() ? "primary" : string.Join(",", targets);

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
