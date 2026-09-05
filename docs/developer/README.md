# Rig2Cast developer documentation

This documentation has two entry paths:

- [Use Rig2Cast in an application](using-rig2cast.md) explains model discovery,
  connection settings, transports, managed sessions, capabilities, controls,
  events, reconnect behavior, and transmit safety.
- [Create a radio driver](driver-development.md) explains the factory and driver
  contracts, protocol boundaries, capabilities, observations, failures, tests,
  and distribution.

Driver authors should also use the [driver test and release
checklist](driver-testing.md). External binary drivers use the separate [plugin
host guide](../plugin-host.md).

For a compact namespace-and-contract inventory, see the [native API reference
map](api-reference.md).

## Project and assembly map

Rig2Cast is a set of cooperating .NET 8 assemblies, not one monolithic DLL.
Reference only the layers an application or driver needs.

| Project | Purpose | Typical consumer |
| --- | --- | --- |
| `Rig2Cast.Abstractions` | Public radio, capability, driver, transport, session, security, event, control, and meter contracts | Every host and driver |
| `Rig2Cast.Core` | Driver/model catalog and shared core services | Application hosts |
| `Rig2Cast.Runtime` | Serialized multi-client radio access, state cache, authorization, leases, events, and reconnect supervision | Application hosts |
| `Rig2Cast.Transports` | Serial and transparent raw-TCP transports plus serial-port discovery | Hosts opening physical/network radios |
| `Rig2Cast.Protocols.Ascii` | Shared semicolon-framed ASCII CAT session | Compatible driver projects |
| `Rig2Cast.Protocols.Civ` | Shared binary Icom-style CI-V framing/session | CI-V driver projects |
| `Rig2Cast.Protocols.Declarative` | Compiled C# command/value descriptors | Drivers with regular command tables |
| `Rig2Cast.PluginHost` | Discovery, trust verification, isolation, and loading of external drivers | Extensible hosts |
| `Rig2Cast.Drivers.*` | Built-in manufacturer/model implementations | Hosts that bundle those radios |

The source tree currently uses project references. Do not assume that every
assembly has been published as a stable NuGet package. Public APIs are still
evolving; pin third-party integrations to a tested commit or release.

## Architectural rules worth reading first

- [Architecture overview](../architecture.md)
- [Capabilities](../capabilities.md)
- [Receiver and VFO model](../architecture/receiver-vfo-model.md)
- [Typed connection settings](../architecture/typed-connection-settings.md)
- [Mode applicability](../architecture/mode-applicability.md)
- [Raw TCP transport](../architecture/raw-tcp-transport.md)
- [Declarative descriptor engine](../declarative-engine.md)

## Working examples

- [`Rig2Cast.CapabilityGui`](../../samples/Rig2Cast.CapabilityGui/README.md): a
  capability-driven Avalonia application.
- [`Rig2Cast.Console`](../console-operating-manual.md): model selection,
  serial/raw-TCP connection, inspection, and control.
- [`Rig2Cast.ExamplePlugin`](../../samples/Rig2Cast.ExamplePlugin/README.md): the
  minimum external driver and manifest.
- [`Rig2Cast.DeclarativeExamplePlugin`](../../samples/Rig2Cast.DeclarativeExamplePlugin/README.md):
  compiled declarative descriptors and conditional capability data.

## Current boundaries

The native API supports serial and transparent raw TCP, capability-driven
controls, managed events, reconnect supervision, and external driver plugins.
The declarative engine is a compiled C# descriptor vocabulary; it is not yet an
external JSON/YAML driver format. A public read-only raw-frame fan-out and an
IC-7300 spectrum stream are roadmap items, not current consumer APIs. Legacy
Yaesu binary CAT is also not implemented yet.
