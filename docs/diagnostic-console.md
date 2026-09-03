# Rig2Cast diagnostic console

For the complete operator-facing command and three-driver test reference, see the
[Rig2Cast Console operating manual](console-operating-manual.md).

The diagnostic console is the supported manual test surface during early development. It can use deterministic simulators or registered physical radios such as the FTDX10, Elecraft K3 family, or IC-7300 pilot. Physical connections are read-only unless write access is explicitly enabled at startup. CAT PTT requires write access and a bounded transmit lease; tuner-start is not exposed.

## Start the console

```powershell
# Simulator, read-only
dotnet run --project samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --simulator

# Simulator with setters
dotnet run --project samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --simulator --allow-write

# Physical FTDX10, read-only
dotnet run --project samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --port COM11 --baud 38400

# Physical FTDX10 with USB-only automatic information enabled
dotnet run --project samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --port COM11 --baud 38400 --auto-information

# Physical FTDX10 with non-transmitting setters
dotnet run --project samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --port COM11 --baud 38400 --allow-write

# List and select a physical Elecraft model
dotnet run --project samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --list-models
dotnet run --project samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --model elecraft.k3s --port COM12 --baud 38400 --allow-write

# Elecraft AI2: typed front-panel control announcements
dotnet run --project samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --model elecraft.k3s --port COM12 --baud 38400 --allow-write --auto-information-mode 2

# IC-7300 CI-V simulator
dotnet run --project samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --model icom.ic-7300 --simulator

# IC-7300 simulator with acknowledged, readback-verified frequency/mode/split setters
dotnet run --project samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --model icom.ic-7300 --simulator --allow-write

# At the interactive prompt: set split on, inspect state, then restore split off
# Safe simulator PTT example: ptt on 2 (automatically returns to RX after two seconds)

# Representative expanded IC-7300 commands at the interactive prompt:
# set passband 2700
# set numeric AfGain 143
# set numeric ClarifierOffsetHz -1250
# set choice Preamp preamp2
# set choice Attenuator 20db
# set choice Agc slow
# set switch NoiseReduction on
# set switch ReceiveClarifier on
# set mode DataUsb
# meters

# Physical IC-7300; override --civ-address if its documented 94h default was changed
dotnet run --project samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --model icom.ic-7300 --port COM13 --baud 19200 --civ-address 94
```

Only one application may own the serial port. Close other CAT software before opening the physical radio.

### External driver plugins

Load trusted plugins before model selection with:

```powershell
dotnet run --project samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --plugin-config .\plugin-host.json --list-models
dotnet run --project samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --plugin-config .\plugin-host.json --model vendor.model --port COM15
```

`--plugin-directory <path>` may be repeated to add directories from the command line.
Production loading still requires matching trust records from `--plugin-config`.
For local development only, `--plugin-development-mode` bypasses hash verification
and prints a warning. Invalid plugins produce diagnostics without hiding built-in or
other valid models. See [plugin-host.md](plugin-host.md) for the strict configuration
schema, hash generation, trust boundary, and lifetime rules.

A plugin model that advertises `Simulator` can be opened without serial settings:

```powershell
dotnet run --project samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --model rig2cast.example.reference-radio --simulator --plugin-directory .\samples\Rig2Cast.ExamplePlugin\bin\Debug\net8.0 --plugin-development-mode
```

Without `--simulator`, the Console rejects simulator-only models before applying a
baud-rate default or attempting to open a serial port.

The FTDX10 manual limits `AI` automatic information to its USB CAT connection. Use
`--auto-information` only with the enhanced USB CAT port. Rig2Cast confirms `AI1;`
at startup and sends `AI0;` during clean shutdown.

For Elecraft radios, `--auto-information` selects AI1 consolidated `IF` state.
Use `--auto-information-mode 2` to request the documented per-control responses
needed for typed front-panel gain, AGC, attenuator, power, and related events.

Physical console connections are supervised. If the radio is powered off or the CAT
cable is removed after startup, watch output reports `Faulted` and `Reconnecting`.
Rig2Cast retries with backoff and creates a fresh connection. After the radio returns,
it verifies identification, restores automatic information when requested, performs a
full state read, and publishes `Connected` without requiring the console to restart.

## Inspect the radio

```text
help
radio
state
refresh
capabilities
capabilities core
capabilities numeric
capabilities switches
capabilities choices
capabilities meters
meters
meters B
passband
passband B
get numeric AfGain
get numeric AfGain B
get switch NoiseReduction
get choice RoofingFilter
get choice FilterWidth
get choice VoxDelay
get choice AudioPeakFilterWidth
get choice TuningStep
watch on
watch off
poll start 500
poll stop
```

`state` and `refresh` perform a live, scheduled radio query. `GetSnapshotAsync()` remains a cache-oriented library operation. Polling refreshes that cache periodically; combine `poll start 500` with `watch on` to see front-panel changes when automatic information is disabled. With `--auto-information`, `watch on` receives supported front-panel changes without polling. Unchanged state does not increment the state revision or publish duplicate state events.

Choice capabilities include stable values, display names, write access, and applicable modes. Numeric capabilities include minimum, maximum, step, unit, and access metadata.

## Test setters

Setters require `--allow-write`. Each command is processed through `ManagedRadio`, followed by a readback that prints the confirmed value.

```text
set frequency A 14250000
set frequency B 7100000
set vfo B
set mode Cw
set passband 500
set split on
set numeric AfGain 128
set numeric AfGain B 36
set choice Attenuator B 10db
set passband B 2400
set numeric IfShiftHz 200
set numeric ClarifierOffsetHz -150
set switch NoiseReduction on
set switch ReceiveClarifier on
set choice Attenuator 6db
set choice RoofingFilter 3khz
set choice FilterWidth 500hz
set choice VoxDelay 500ms
set choice AudioPeakFilterWidth medium
set choice TuningStep 10hz
```

The runtime rejects out-of-range numeric values, unknown choices, read-only choices, and filter widths that do not apply to the current mode. Use `capabilities` before changing a control.

## Safety boundary

- Read-only is the default.
- `--allow-write` enables lease-protected CAT PTT. `ptt on` renews a short lease until `ptt off`; `ptt on <seconds>` performs a bounded 1-60 second transmission.
- Continuous PTT renews a 10-second lease every five seconds. Renewal failure, lease expiry, session close, or normal Console shutdown forces RX.
- Tuner-start, radio power, memory writes, and message playback remain unavailable.
- The runtime serializes all operations and confirms typed control writes.
- Exiting disposes the logical session, managed radio, driver, and serial connection.
