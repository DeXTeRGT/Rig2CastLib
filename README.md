# Rig2Cast Lib

An open-source, modern C# framework for amateur-radio transceiver control.

Rig2Cast Lib is a modular .NET 8 framework intended for C# applications,
desktop software, web interfaces, background services, and network clients. It
provides a common radio abstraction, safe multi-client runtime, pluggable model
drivers, capability discovery, and optional protocol adapters.

> **Project status:** Active early development. The architecture and working
> FTDX10 vertical slice are in place, but public APIs are not yet stable. The
> Yaesu FTDX10 is the first supported and physically tested transceiver. An
> initial Elecraft K3S/K3/KX3/KX2 core driver is implemented from the official
> programmer's reference and awaits physical K3S validation.

## Why another radio-control framework?

[Hamlib](https://hamlib.github.io/) has served the amateur-radio community
extremely well. It supports an impressive number of radios and represents
decades of experience with CAT protocols and physical hardware.

Hamlib's native C architecture, however, does not always integrate naturally
into managed .NET applications. Using it from C# commonly involves native
binaries, platform-specific deployment, an interop layer, and additional
synchronization around shared radio access. Modern applications may also need to
expose one radio safely to several clients through native APIs, REST, gRPC, TCP,
desktop applications, and web interfaces.

Rig2Cast Lib approaches this from a new architectural direction. It is not
intended to replace Hamlib and is not a line-by-line C# port. It is an independent
reconsideration of radio control for modern .NET applications.

Official manufacturer documentation is the authority for CAT protocol
implementation. Hamlib is useful as a reference for expected behavior and is an
important compatibility target, but Rig2Cast's architecture and implementation
are developed independently from the available radio CAT protocols. See
[protocol provenance](docs/protocol-provenance.md) for details.

## Design objectives

### Native .NET 8 and C#

Rig2Cast is designed to be embedded naturally in managed C# applications without
requiring a native C interop layer.

### One radio, multiple interfaces

The same managed radio instance is intended to support optional interfaces such
as:

- Native C# API
- REST API *(planned)*
- gRPC *(planned)*
- Richer native TCP protocols *(planned)*
- Hamlib-compatible `rigctld` TCP adapter *(initial implementation available)*
- Desktop and web applications
- Background services

These are adapters around the same runtime and capability model. Manufacturer
CAT logic stays inside the radio driver instead of being duplicated across each
API and user interface.

```text
Native C#  REST  gRPC  TCP  rigctld  Desktop/Web UI
     \       |     |    |      |          /
              Rig2Cast runtime
          sessions, roles, leases,
       capabilities, events, scheduling
                       |
            selected radio driver
                       |
             Serial / future TCP
                       |
                Physical radio
```

### Reliable serialized CAT access

A physical CAT connection is fundamentally sequential. Multiple applications
cannot safely send arbitrary commands to the same serial port simultaneously.

Rig2Cast provides one managed command scheduler per physical radio. Multiple
logical clients can operate concurrently while actual CAT communication remains
serialized, ordered, and protected. This provides real multi-client access
without pretending the underlying serial connection can execute commands in
parallel.

### Multi-client safety and coordination

Every application or network connection receives a logical identity and session.
The runtime provides:

- Client roles and permissions
- Ordered command execution and priorities
- Exclusive operations
- Lease-based access for sensitive operations
- Session cleanup on disconnect
- Safe coordination between concurrent clients

Composite changes can be transactional at the radio-operation level. Changing
mode and passband, for example, can run under exclusive control so another client
cannot insert a command between the two changes. Transmit operations receive
additional protection and are not treated as ordinary setters.

### Capability-driven applications

Every driver publishes a structured description of the connected radio. Generic
applications can discover:

- Available VFOs and operating modes
- Frequency ranges and tuning steps
- Readable and writable features
- Passband, roofing-filter, and other selectable choices
- Numeric controls with minimum, maximum, step, and unit
- Switch controls and meters
- Required permissions or leases
- Features unsupported by the selected radio

A desktop or web interface can configure itself from these capabilities instead
of hard-coding every model. If a radio supports VFO B, the interface can expose
it; otherwise, the same interface can hide or disable it.

### Modular and pluggable radio drivers

Radio models use stable identifiers such as `yaesu.ftdx10`. Each factory
publishes manufacturer, model, driver version, supported transports, baud rates,
and defaults.

Adding an Icom, Elecraft, Ten-Tec, Kenwood, or another Yaesu transceiver follows
the same contracts and development pattern. A new driver should not require
rewriting REST, gRPC, TCP, or application code; consumers continue through the
common abstraction and query the selected radio's capabilities.

### Not limited by the Hamlib protocol

Rig2Cast's native abstraction is not restricted to features representable by the
legacy `rigctld` protocol. New functionality can remain available through the
native C# API and future REST, gRPC, TCP, desktop, or web adapters even when no
Hamlib command exists for it.

Shared concepts belong in common typed contracts. Truly manufacturer-specific
functionality can use optional namespaced extensions without polluting every
driver interface.

## Optional Hamlib compatibility

Rig2Cast includes a separate optional `rigctld`-compatible TCP adapter. Existing
Hamlib-compatible clients can connect while Rig2Cast manages the physical radio,
logical sessions, and serialized CAT access underneath.

The current adapter supports multiple simultaneous TCP clients and commands for:

- Frequency and active VFO
- Operating mode and mode-aware passband
- Split operation and PTT-state reading
- Short and long command forms
- Extended responses

It listens on loopback and operates read-only by default. Writes must be enabled
explicitly, and PTT writes remain unavailable through this initial compatibility
adapter. The objective is practical compatibility without weakening Rig2Cast's
safety model. See the [rigctld adapter guide](docs/rigctld-adapter.md).

## Current FTDX10 support

The Yaesu FTDX10 is the first implemented and physically tested radio. Native
support currently includes:

- Identification, VFO A/B frequency, and active-VFO selection
- Operating modes, split, CAT PTT, and PTT-state reading
- Mode-aware passband and roofing-filter selection
- AF/RF gain, squelch, microphone gain, and transmit power
- Speech processor, noise blanker, noise reduction, monitor, and VOX
- Anti-VOX and discrete VOX delay
- RIT, XIT, and clarifier offset
- IF shift, manual/automatic notch, and contour
- Audio peak filter state, offset, and width
- Attenuator, preamplifier, and AGC choices
- CW pitch and keyer speed
- Mode-aware VFO tuning steps
- Raw S-meter, ALC, SWR, compression, output-power, drain-current, and
  drain-voltage meters
- Structured runtime capability discovery

Meter values remain explicitly raw and uncalibrated until independent hardware
measurements support reliable engineering-unit conversions.

An FTDX10 simulator is included so applications and runtime behavior can be
developed without physical hardware. Detailed implementation status is available
in the [FTDX10 coverage matrix](docs/ftdx10-coverage.md).

## Initial Elecraft K3-family support

The selectable `elecraft.k3s`, `elecraft.k3`, `elecraft.kx3`, and `elecraft.kx2`
profiles currently support model verification, VFO A/B frequencies, operating
mode, explicit split/transmit VFO, PTT state/control, AI1 unsolicited state, AF/RF
gain, requested transmit power, RIT/XIT, AGC speed, attenuator and preamplifier
selection, and capability discovery. KX2-specific mode limitations and connected
K3/K3S option-dependent capabilities are announced separately. The core slice has
been validated against a physical K3S; the newer control batch is awaiting its
interactive hardware pass. See the
[Elecraft protocol record](docs/protocol-sources/elecraft-k3-family.md).

## Quick start

### Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows or Linux for simulator development
- For physical radio use: an available CAT serial port and matching baud rate

```powershell
git clone https://github.com/DeXTeRGT/Rig2CastLib.git
cd Rig2CastLib
dotnet build Rig2Cast.sln
dotnet test tests\Rig2Cast.Runtime.Tests\Rig2Cast.Runtime.Tests.csproj
```

The current suite contains **192 automated tests** covering CAT framing and
parsing, runtime serialization, concurrent clients, roles and leases, capability
and model discovery, trusted plugin loading, TCP behavior, disconnect/reconnect behavior, unsolicited
reporting, shutdown cleanup, FTDX10 controls, and the initial Elecraft K3-family
protocol slice.

The diagnostic Console can add trusted external driver assemblies to the same model
catalog used by built-in drivers. Use `--plugin-config <file>` and then
`--list-models`; see the [plugin host guide](docs/plugin-host.md) for the strict JSON
schema and SHA-256 trust workflow. `--plugin-development-mode` is only for trusted
local development and bypasses binary-hash verification.

### Run the simulator demo

```powershell
dotnet run --project samples\Rig2Cast.Demo\Rig2Cast.Demo.csproj
```

### Explore capabilities interactively

```powershell
dotnet run --project samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --simulator
```

Try these commands:

```text
radio
state
capabilities
capabilities numeric
capabilities switches
capabilities choices
capabilities meters
get numeric AfGain
get numeric AfGain B
get choice FilterWidth
get choice VoxDelay
get choice AudioPeakFilterWidth
get choice TuningStep
meters
meters B
passband B
```

Use the simulator with non-transmitting setters:

```powershell
dotnet run --project samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --simulator --allow-write
```

Example commands:

```text
set frequency A 14250000
set vfo B
set mode Cw
set split on
set numeric CwPitchHz 700
set numeric KeyerSpeedWpm 20
set numeric AfGain B 36
set choice Attenuator B 10db
set passband B 2400
set numeric AudioPeakFilterOffsetHz 50
set choice FilterWidth 500hz
set choice VoxDelay 500ms
set choice AudioPeakFilterWidth medium
set choice TuningStep 10hz
```

The console exposes CAT PTT only with `--allow-write`, an exclusive time-limited
transmit lease, verified hardware-state readback, and automatic RX cleanup. It
continues to withhold tuner-start operations. See the
[diagnostic console guide](docs/diagnostic-console.md) for all commands and safety
details.

### Connect a physical FTDX10

Only one application can own a serial port. Close other CAT programs first, then
replace the example port and baud rate as necessary:

```powershell
# Read-only by default
dotnet run --project samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --port COM11 --baud 38400

# Enhanced USB CAT port: receive supported front-panel changes without polling
dotnet run --project samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --port COM11 --baud 38400 --auto-information

# Explicitly enable non-transmitting setters
dotnet run --project samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --port COM11 --baud 38400 --allow-write
```

A read-only hardware smoke test is also available:

```powershell
dotnet run --project samples\Rig2Cast.Ftdx10Smoke\Rig2Cast.Ftdx10Smoke.csproj -- --port COM11 --baud 38400
```

### Connect a physical Elecraft K3-family radio

First list the stable model identifiers, then select the exact radio. Replace the
port as appropriate; serial framing is taken from the model descriptor.

```powershell
dotnet run --project samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --list-models
dotnet run --project samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --model elecraft.k3s --port COM12 --baud 38400
dotnet run --project samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- --model elecraft.k3s --port COM12 --baud 38400 --auto-information
```

Add `--allow-write` only when the radio is safely configured for setter testing.
The console deliberately does not expose transmit commands.

### Run the rigctld-compatible server

List registered models without opening hardware:

```powershell
dotnet run --project samples\Rig2Cast.RigctldHost -- --list-models
```

Run against the simulator:

```powershell
dotnet run --project samples\Rig2Cast.RigctldHost -- --model yaesu.ftdx10 --simulator
```

Run against a physical FTDX10, read-only by default:

```powershell
dotnet run --project samples\Rig2Cast.RigctldHost -- --model yaesu.ftdx10 --serial-port COM11 --baud 38400
```

The same adapter can select the initial Elecraft driver:

```powershell
dotnet run --project samples\Rig2Cast.RigctldHost -- --model elecraft.k3s --serial-port COM12 --baud 38400
```

The default endpoint is `127.0.0.1:4532`. Add `--allow-write` to enable supported
non-PTT setters. See the [rigctld guide](docs/rigctld-adapter.md) for commands,
raw TCP examples, multi-client limits, and network safety notes.

## Transparent about AI-assisted development

The requirements, objectives, architectural direction, safety decisions, and
feature priorities for Rig2Cast were defined by the project owner. The hardware
environment, physical FTDX10 testing, and final technical decisions also remain
under human control.

A significant part of the code, tests, and documentation has been produced with
AI coding assistance through an iterative process of specification,
implementation, review, correction, automated testing, and physical validation.
This is an **AI-assisted project, not an unattended automatically generated
one**.

AI-assisted development under human supervision makes it possible to address a
project of this size in a practical timeframe instead of spending many years
before releasing a useful foundation. Generated code is treated like any other
contribution: it must be reviewed, compiled, tested, and—where hardware behavior
is involved—verified against the transceiver and its official documentation.

The project should ultimately be judged by its architecture, code quality,
reproducible behavior, tests, documented protocol sources, peer review, and real
hardware validation—not merely by whether every line was typed manually.
Independent review is especially valuable for an AI-assisted project.

## Open source and collaboration

Rig2Cast Lib is licensed under the **GNU Affero General Public License v3.0
(AGPL-3.0-only)**. See [LICENSE](LICENSE) and [NOTICE](NOTICE).

The project will continue to be actively developed because it is directly useful
to its owner. Community participation is welcome but is not a condition for
continued development.

Contributions and constructive feedback are welcome for:

- Additional transceiver drivers and hardware testing
- CAT protocol verification and Hamlib compatibility
- Native .NET, REST, gRPC, TCP, desktop, and web interfaces
- Documentation, examples, Windows, and Linux deployment
- Concurrency, reliability, reconnect, and failure recovery
- Security and remote-access design

The objective is not to compete with or diminish Hamlib. Hamlib remains an
important project, an invaluable source of experience, and a major compatibility
target. Rig2Cast explores radio control designed specifically for modern .NET:
asynchronous, modular, capability-driven, multi-client safe,
transport-independent, and easy to extend.

Before contributing, please read [CONTRIBUTING.md](CONTRIBUTING.md), the
[architecture overview](docs/architecture.md), and the
[driver-development guide](docs/driver-development.md).

Repository: <https://github.com/DeXTeRGT/Rig2CastLib>

73!
