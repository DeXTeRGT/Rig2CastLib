# Rig2Cast diagnostic console

The diagnostic console is the supported manual test surface during early development. It can use the deterministic simulator or a physical FTDX10. Physical connections are read-only unless write access is explicitly enabled at startup. PTT and tuner-start are not exposed by this console.

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
```

Only one application may own the serial port. Close other CAT software before opening the physical radio.
The FTDX10 manual limits `AI` automatic information to its USB CAT connection. Use
`--auto-information` only with the enhanced USB CAT port. Rig2Cast confirms `AI1;`
at startup and sends `AI0;` during clean shutdown.

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
get numeric AfGain
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
set split on
set numeric AfGain 128
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
- `--allow-write` does not enable PTT.
- PTT, tuner-start, radio power, memory writes, and message playback are unavailable.
- The runtime serializes all operations and confirms typed control writes.
- Exiting disposes the logical session, managed radio, driver, and serial connection.
