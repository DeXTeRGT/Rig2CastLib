# Rig2Cast Console operating manual

This guide is for radio amateurs who want to inspect and manually test the three
built-in CAT driver families:

- Yaesu FTDX10 (`yaesu.ftdx10`)
- Elecraft K3S, K3, KX3, and KX2 (`elecraft.k3s`, `elecraft.k3`,
  `elecraft.kx3`, `elecraft.kx2`)
- Icom IC-7300 (`icom.ic-7300`)

The Console is a diagnostic and validation tool. It is not intended to replace a
logging program or full station-control application.

## 1. Safety first

The Console starts read-only unless `--allow-write` is specified. In read-only mode,
frequency, mode, control, split, and PTT writes are rejected.

Before testing writes on a physical radio:

1. Save important radio settings.
2. Select a legal frequency and appropriate mode.
3. Disconnect amplifiers or place them in standby.
4. For PTT tests, use a rated dummy load and the lowest practical RF power.
5. Keep the radio's front-panel PTT or power switch within reach.
6. Close other CAT programs. Only one application can normally own a serial port.

CAT PTT is protected by a transmit lease. `ptt on <seconds>` automatically returns
the radio to RX after 1–60 seconds. Continuous PTT renews a short lease and is forced
off when the lease expires, the session closes, or the Console shuts down normally.
This protection supplements normal station safety; it does not replace it.

The Console does not expose radio power commands, memory writes, message playback,
or antenna-tuner start. The FTDX10 `AntennaTuner` switch controls tuner enable/status;
it does not initiate a tuning cycle.

## 2. Building and starting the Console

From the repository root:

```powershell
dotnet build .\samples\Rig2Cast.Console\Rig2Cast.Console.csproj
```

Run through the .NET SDK:

```powershell
dotnet run --project .\samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- <options>
```

The `--` separates `dotnet run` options from Rig2Cast options. When running the built
executable directly, omit that separator:

```powershell
.\samples\Rig2Cast.Console\bin\Debug\net8.0\Rig2Cast.Console.exe <options>
```

List all registered models:

```powershell
dotnet run --project .\samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --list-models
```

If `--model` is omitted, the Console selects `yaesu.ftdx10`. Physical connections
default to `COM11`; specify the actual port rather than relying on this convenience.

## 3. Startup options

| Option | Parameter | Meaning |
|---|---:|---|
| `--list-models` | none | Lists registered model IDs and exits. |
| `--model` | model ID | Selects a built-in or plugin model. |
| `--port` | port name | Physical serial port, for example `COM7`. Default is `COM11`. |
| `--baud` | integer | CAT baud rate. If omitted, the selected model's default is used. |
| `--simulator` | none | Uses a supported in-process simulator instead of a serial port. |
| `--allow-write` | none | Opens an Operator session and permits supported mutations. |
| `--auto-information` | none | Enables supported unsolicited CAT reporting. |
| `--auto-information-mode` | `0`–`3` | Selects an Elecraft AI mode; specifying it also enables automatic information. |
| `--civ-address` | hex byte | IC-7300 radio address, for example `94` or `0x94`. |
| `--plugin-config` | path | Loads the strict plugin-host configuration file. |
| `--plugin-directory` | path | Adds a plugin directory; may be repeated. |
| `--plugin-development-mode` | none | Bypasses plugin SHA-256 verification for trusted local development only. |

Option names are case-insensitive. Model IDs and serial-port names should be entered
exactly as displayed by `--list-models` and the operating system.

### Serial settings used by the built-in drivers

| Driver | Supported baud rates | Default | Serial format |
|---|---|---:|---|
| FTDX10 | 4800, 9600, 19200, 38400 | 38400 | 8 data bits, no parity, 2 stop bits, RTS/CTS |
| Elecraft K3 family | 4800, 9600, 19200, 38400 | 38400 | 8 data bits, no parity, 1 stop bit, no handshake |
| IC-7300 | 4800, 9600, 19200, 38400, 57600, 115200 | 19200 | 8 data bits, no parity, 1 stop bit, no handshake |

The selected rate must match the radio menu. IC-7300 rates above 19200 require the
appropriate unlinked USB CI-V configuration. The default IC-7300 CI-V radio address
is hexadecimal `94` and the driver controller address is `E0`.

## 4. Starting each built-in radio

### Yaesu FTDX10

Simulator, read-only:

```powershell
dotnet run --project .\samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --model yaesu.ftdx10 --simulator
```

Simulator with writes:

```powershell
dotnet run --project .\samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --model yaesu.ftdx10 --simulator --allow-write
```

Physical USB CAT connection:

```powershell
dotnet run --project .\samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --model yaesu.ftdx10 --port COM7 --baud 38400 --auto-information --allow-write
```

FTDX10 automatic information is intended for the enhanced USB CAT connection. The
driver enables AI at startup and disables it during clean shutdown.

### Elecraft K3 family

Select the exact radio model:

```powershell
dotnet run --project .\samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --model elecraft.k3s --port COM8 --baud 38400 --allow-write
dotnet run --project .\samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --model elecraft.k3 --port COM8 --baud 38400 --allow-write
dotnet run --project .\samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --model elecraft.kx3 --port COM8 --baud 38400 --allow-write
dotnet run --project .\samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --model elecraft.kx2 --port COM8 --baud 38400 --allow-write
```

AI1 produces consolidated state reports. AI2 is preferable when testing typed
front-panel gain, power, AGC, attenuator, preamp, RIT/XIT, and similar events:

```powershell
dotnet run --project .\samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --model elecraft.k3s --port COM8 --baud 38400 --auto-information-mode 2 --allow-write
```

The Console catalog currently advertises the Elecraft models as simulator-capable,
but the Console does not yet provide a radio-side Elecraft protocol simulator. Use a
physical radio for this procedure. The FTDX10 and IC-7300 simulator paths are fully
wired.

### Icom IC-7300

Simulator with writes:

```powershell
dotnet run --project .\samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --model icom.ic-7300 --simulator --allow-write
```

Physical radio using its default address:

```powershell
dotnet run --project .\samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --model icom.ic-7300 --port COM9 --baud 19200 --civ-address 94 --allow-write
```

Change `--civ-address` if the radio's CI-V address was changed. CI-V Transceive is
useful for `watch on`; USB Echo Back may be on or off because the CI-V session handles
both conditions.

## 5. Interactive command conventions

After connection, commands are entered at the `rig2cast>` prompt.

- Command names, enum names, modes, and `on`/`off` are case-insensitive.
- Choice values should be copied exactly from `capabilities choices`; lowercase is
  recommended.
- Frequencies, passbands, and offsets are in hertz unless stated otherwise.
- Numeric ranges and units vary by driver. Inspect `capabilities numeric` first.
- A target in square brackets is optional. Do not type the brackets.
- Valid Boolean values are `on`, `off`, `true`, `false`, `1`, and `0`.
- `main` and `sub` are receiver identities. `A`, `B`, and `Current` are VFO identities.
- A syntactically valid target may still be unsupported by the selected radio. The
  capabilities output is authoritative.

## 6. Read and inspection commands

### `help`

Prints the compact built-in command summary.

```text
help
```

### `radio`

Shows manufacturer, model, driver/version, connection, and whether writes are
enabled.

```text
radio
```

### `state` and `refresh`

Both perform a live radio query and print connection state, active/transmit VFO,
signal paths, frequencies, mode, split, and PTT state.

```text
state
refresh
```

These are not cache-only displays. A CAT failure can therefore produce an error or
start the reconnect supervisor.

### `capabilities` / `caps`

Displays what the connected driver actually supports. This should be the first
command used after connecting.

```text
capabilities
caps
capabilities core
capabilities numeric
capabilities switches
capabilities choices
capabilities meters
```

`core` includes VFOs, receiver targets, modes, PTT, and mode-dependent passband
constraints. Other categories list valid names, ranges, units, targets, access, and
choice values. `capabilities` without a category prints everything.

### `meters [target]`

Reads every advertised meter that is valid in the present state.

```text
meters
meters main
meters A
meters B
```

Transmit-only meters are skipped while receiving. Meter output contains the raw
value and a normalized percentage. Unless `calibrated=True` is shown by capabilities,
do not interpret the percentage as watts, dBm, amperes, volts, or an exact S-unit.

### `passband [target]`

Reads the current DSP/filter passband width:

```text
passband
passband main
passband A
passband B
```

Only use targets shown by `capabilities core`. IC-7300 adjustable passband is
available for AM, LSB/USB, DATA LSB/USB, CW/CW-R, and RTTY/RTTY-R, but not FM.

### `get numeric <name> [target]`

Reads a numeric control:

```text
get numeric AfGain
get numeric RfGain main
get numeric ClarifierOffsetHz
```

Names, targets, ranges, steps, and units come from `capabilities numeric`.

### `get switch <name> [receiver]`

Reads an on/off feature:

```text
get switch NoiseReduction
get switch ReceiveClarifier main
```

Switch targets are receiver identities (`main` or an advertised `sub`), not VFO
identities.

### `get choice <name> [target]`

Reads a discrete selection:

```text
get choice Agc
get choice Attenuator A
get choice Preamp main
```

Use `capabilities choices` to see the stable values accepted for each model and
target. Option-dependent Elecraft choices can differ between main and sub receivers.

## 7. Write commands

All commands in this section require `--allow-write`. Rig2Cast validates capability,
target, range, and access before invoking the driver. The Console then reads back and
prints the confirmed result.

### `set frequency <target> <hz>`

```text
set frequency A 14250000
set frequency B 7100000
set frequency main 14074000
set frequency Current 7100000
```

- FTDX10: use VFO `A` or `B`; `main` addresses the selected main receiver.
- Elecraft: use `A` or `B`; `main` follows the active receive path. `sub` is available
  only when the detected radio/options expose it.
- IC-7300: use `main` or `Current`. Stable A/B identity is intentionally not exposed.

### `set vfo <A|B>`

Selects the active VFO when the driver advertises writable VFO selection:

```text
set vfo A
set vfo B
```

This is implemented for FTDX10. It is deliberately unavailable for the Elecraft
K3-family because its `FR` behavior is tied to split rather than a conventional
active receive-VFO selector. It is unavailable for IC-7300 because the documented
CI-V commands cannot reliably query stable A/B identity after reconnect or
front-panel changes.

### `set mode [receiver] <mode>`

Without a receiver, changes the active/main operating mode. With a receiver, changes
that advertised receiver:

```text
set mode Usb
set mode main DataUsb
set mode sub Cw
```

Mode enum names used across the drivers are:

```text
Lsb Usb Cw CwReverse Am AmNarrow Fm FmNarrow
DataLsb DataUsb DataFm DataFmNarrow Psk Rtty RttyReverse
```

Not every radio supports every value. KX2 excludes FM. IC-7300 currently exposes
LSB, USB, AM, CW, CW-R, RTTY, RTTY-R, FM, DATA LSB, DATA USB, and DATA FM. Always
check `capabilities core`.

### `set passband [target] <hz>`

Sets a supported passband width:

```text
set passband 2400
set passband main 2700
set passband A 500
```

The width must be listed for the current mode by `capabilities core`. FTDX10 and
IC-7300 use discrete mode-dependent widths. Elecraft accepts its advertised range and
may quantize the requested value to a radio-supported step; the readback is the
confirmed value.

### `set split <on|off>`

```text
set split on
set split off
```

FTDX10 transmits on the VFO opposite the active receive VFO when split is enabled.
The Console's simple Elecraft split command selects VFO B for transmit; disabling
split restores normal receive/transmit behavior. IC-7300 toggles the radio's current
split setting but does not assign stable A/B names.

### `set numeric <name> [target] <value>`

```text
set numeric AfGain 128
set numeric RfGain main 200
set numeric TransmitPower 10
set numeric ClarifierOffsetHz -150
```

The value is an integer. Observe the unit: some controls are raw radio units, others
are percent, watts, hertz, WPM, or discrete steps. Out-of-range or misaligned values
are rejected.

### `set switch <name> [receiver] <on|off>`

```text
set switch NoiseReduction on
set switch ReceiveClarifier main on
set switch ManualNotch off
```

The optional target is a receiver (`main`/`sub`). Use `capabilities switches` to avoid
changing a feature not implemented by that driver.

### `set choice <name> [target] <value>`

```text
set choice Agc slow
set choice Attenuator A 10db
set choice Preamp main preamp1
set choice RoofingFilter 3khz
```

Choice values are driver-specific. Copy a writable value from `capabilities choices`.
Some values are read-only representations of combined radio state and cannot be set.

## 8. PTT commands

### `ptt status`

Reads the hardware state without requiring `--allow-write`:

```text
ptt status
```

### Bounded PTT

The recommended test form is:

```text
ptt on 2
```

The duration must be 1–60 seconds. The Console acquires a transmit lease, verifies
that the radio entered TX, and automatically forces RX when the duration expires.
After waiting, confirm:

```text
ptt status
state
```

### Continuous PTT

```text
ptt on
ptt off
```

Continuous mode renews a short lease until `ptt off`. Use it only when deliberately
testing a sustained carrier or when the selected mode does not produce a carrier by
itself. Normal shutdown, loss of lease, or renewal failure requests RX automatically.

PTT is implemented for all three built-in driver families. It always requires
`--allow-write` and never bypasses the managed runtime safety path.

## 9. Events and polling

### `watch [on|off]`

Starts or stops printing Rig2Cast events:

```text
watch on
watch off
```

The default action is `on`. Events include connection transitions and changed radio
state. Supported unsolicited CAT reports may update state without polling.

### `poll start [milliseconds]` / `poll stop`

```text
poll start
poll start 500
poll stop
```

The default interval is 500 ms; the valid range is 100–60000 ms. Polling performs
live state refreshes. Combine it with `watch on` to display front-panel changes when
automatic information is unavailable or disabled:

```text
watch on
poll start 500
```

Avoid unnecessarily fast polling on slow serial links. Stop polling before intensive
manual command testing if response timing becomes difficult to interpret.

## 10. Model-specific command reference

### FTDX10

Core targets are VFO A/B and receiver `main`. Implemented controls include:

- Numeric: `AfGain`, `RfGain`, `Squelch`, `MicrophoneGain`, `TransmitPower`,
  `SpeechProcessorLevel`, `NoiseReductionLevel`, `NoiseBlankerLevel`, `MonitorLevel`,
  `VoxGain`, `AntiVoxLevel`, `IfShiftHz`, `ManualNotchFrequencyHz`,
  `ContourFrequencyHz`, `ClarifierOffsetHz`, `CwPitchHz`, `KeyerSpeedWpm`, and
  `AudioPeakFilterOffsetHz`.
- Switches: `NoiseBlanker`, `NoiseReduction`, `Monitor`, `SpeechProcessor`, `Vox`,
  `DialLock`, `BreakIn`, `AntennaTuner`, `NarrowFilter`, `AutoNotch`, `ManualNotch`,
  `Contour`, `AudioPeakFilter`, `ReceiveClarifier`, and `TransmitClarifier`.
- Choices: `Attenuator`, `Preamp`, `Agc`, `RoofingFilter`, `FilterWidth`, `VoxDelay`,
  `AudioPeakFilterWidth`, and `TuningStep`.
- Meters: `SignalStrength`, `Compression`, `Alc`, `Power`, `Swr`, `DrainCurrent`, and
  `DrainVoltage`.

Useful FTDX10 test sequence:

```text
radio
capabilities
state
set frequency A 14250000
set frequency B 7100000
set vfo A
set mode Usb
set passband 2700
set numeric AfGain 128
set choice Attenuator off
set choice Preamp amp1
set choice Agc auto
set switch NoiseReduction on
get switch NoiseReduction
meters
set split on
state
set split off
```

### Elecraft K3/K3S/KX3/KX2

Implemented core features include VFO A/B frequency, operating mode, split transmit
VFO behavior, PTT, passband, AI reports, and option-aware capabilities.

- Numeric: `AfGain`, `RfGain`, `TransmitPower`, `ClarifierOffsetHz`, and
  `KeyerSpeedWpm`.
- Switches: `ReceiveClarifier` and `TransmitClarifier`.
- Choices: `Agc`, `Attenuator`, and `Preamp`.
- Meters: `SignalStrength`; `Swr` is exposed on supported firmware/hardware and is a
  transmit-time reading.
- Passband: 10 Hz API resolution over the advertised range; the radio may quantize.

Options detected during connection affect maximum transmit power, the presence of a
sub receiver, preamp choices, attenuator choices, and meter targets. For example, K3S
main attenuator choices can include `off`, `5db`, `10db`, and `15db`; a sub receiver
uses its own advertised choices. Never assume an option—read capabilities.

Useful Elecraft test sequence:

```text
radio
capabilities
state
set frequency A 14060000
set frequency B 14100000
set mode Cw
set numeric KeyerSpeedWpm 20
set numeric TransmitPower 5
set choice Agc fast
get choice Attenuator A
get choice Preamp main
set switch ReceiveClarifier on
set numeric ClarifierOffsetHz 250
passband
meters
set split on
state
set split off
```

`set vfo` is not supported by this family driver. Use frequency/mode targets and
split controls according to the capability output.

### IC-7300

The driver exposes one receiver (`main`) and `VfoId.Current`; it does not fabricate
A/B identity. Implemented controls are:

- Numeric: `AfGain`, `RfGain`, `Squelch`, `TransmitPower`, `NoiseReductionLevel`,
  `NoiseBlankerLevel`, and `ClarifierOffsetHz`.
- Switches: `NoiseBlanker`, `NoiseReduction`, `AutoNotch`, `ManualNotch`,
  `ReceiveClarifier`, and `TransmitClarifier`.
- Choices: `Attenuator` (`off`, `20db`), `Preamp` (`off`, `preamp1`, `preamp2`), and
  `Agc` (`fast`, `medium`, `slow`).
- Meters: `SignalStrength`, `Power`, `Swr`, and `Alc`, reported as CI-V raw 0–255 and
  normalized values without calibrated engineering units.
- DATA modes: `DataLsb`, `DataUsb`, and `DataFm`.
- Adjustable passband: 200–10000 Hz in 200 Hz steps for AM; for SSB, DATA SSB, CW,
  and RTTY, 50–500 Hz in 50 Hz steps followed by 600–3600 Hz in 100 Hz steps.

Useful IC-7300 simulator test sequence:

```text
radio
capabilities
state
set frequency main 14074000
set mode main DataUsb
set passband main 2700
set numeric AfGain 143
set numeric RfGain 200
set numeric Squelch 30
set numeric TransmitPower 25
set choice Preamp preamp1
set choice Attenuator 20db
set choice Agc slow
set switch NoiseBlanker on
set numeric NoiseBlankerLevel 80
set switch NoiseReduction on
set numeric NoiseReductionLevel 100
set numeric ClarifierOffsetHz -1250
set switch ReceiveClarifier on
meters
set split on
state
set split off
```

For a safe simulated PTT check:

```text
ptt status
ptt on 2
ptt status
```

Wait at least two seconds and run `ptt status` again; it should report RX/off.

## 11. Reconnection behavior

Physical connections are supervised. If the radio is powered off or the CAT cable is
removed, events can report `Faulted` and `Reconnecting`. Rig2Cast retries with
backoff, creates a fresh driver/transport, verifies identification, restores requested
automatic-information mode, refreshes state, and reports `Connected` when successful.

Use:

```text
watch on
```

before a cable-removal or power-cycle test. Do not perform a reconnect test while
transmitting.

## 12. Errors and troubleshooting

### “Writes are disabled”

Restart the Console with `--allow-write`. Do this only when the station is safe.

### “Baud rate ... is not supported”

Use one of the model's listed rates and make the radio menu match. Run
`--list-models` to see defaults.

### Access denied or port already in use

Close WSJT-X, logging software, vendor utilities, virtual-port clients, and any older
Rig2Cast Console process using the same COM port.

### Identification or CI-V address failure

- Confirm the exact model ID.
- Confirm port, baud, data bits, parity, stop bits, and handshake.
- For IC-7300, verify the configured CI-V address and `--civ-address` value.
- Ensure no software is changing the radio's CAT menu while connecting.

### A control or target is rejected

Run the relevant capability category:

```text
capabilities core
capabilities numeric
capabilities switches
capabilities choices
capabilities meters
```

Use the displayed target, range, mode, and writable choice value. A feature supported
by one driver is not automatically supported by the others.

### Meter is skipped

Power, SWR, ALC, compression, current, or voltage may be transmit-only. The Console
skips meters marked `RequiresTransmit` while RX. Do not key a physical transmitter
solely to make a diagnostic meter appear.

### Events do not appear after a front-panel change

Enable the appropriate automatic-information mode when supported, or use:

```text
watch on
poll start 500
```

Some protocols do not emit every property change. Polling remains the fallback.

## 13. Ending a session

```text
quit
exit
```

Either command performs normal asynchronous cleanup. `Ctrl+C` requests the same
shutdown path. If continuous PTT was active, cleanup requests RX before releasing the
radio and serial connection.

