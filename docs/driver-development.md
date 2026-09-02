# Driver development

## Model discovery

Every driver factory declares one or more `RadioModelDescriptor` records in its
`RadioDriverDescriptor`. Model IDs are stable, case-insensitive identifiers such
as `yaesu.ftdx10`; they are not centralized enums. This lets a new driver plugin
add models without modifying Rig2Cast abstractions.

Hosts register factories in `RadioDriverCatalog`. The catalog can list installed
models before any transport is opened, then resolve the selected model to its
factory. Static catalog metadata describes transports and connection defaults;
after connection, the driver's `RadioCapabilities` remain authoritative for the
features available from that particular radio instance.

A plugin exposes complete static model metadata through a sidecar manifest so it can
be discovered without loading its assembly. Loading requires an explicit trust record
containing plugin ID and SHA-256 binary hash; development mode may opt out. The host
verifies that the loaded factory describes exactly the same models, transports, baud
rates, and defaults. See [plugin-host.md](plugin-host.md).

The external driver API version is a canonical `major.minor` value. The initial SDK
uses exact matching: manifest API, factory API, and host API must all be `1.0`.
Neither forward nor backward compatibility is implied, and build/revision forms such
as `1.0.0` are not aliases. Use `RadioDriverApiCompatibility.CurrentVersion` when
host code needs the current contract value; external factory metadata must declare
the literal API version against which it was built.

Drivers compose transport, framing/codec, manufacturer or protocol-family behavior, and model-specific capability/quirk declarations. Declarative descriptions are encouraged for regular commands, while exceptional behavior remains expressible in C#. See [declarative-engine.md](declarative-engine.md) for the validated descriptor boundary and incremental roadmap.

The independent [example plugin](../samples/Rig2Cast.ExamplePlugin/README.md)
demonstrates the minimum external SDK and manifest workflow. It is intentionally a
read-only virtual device and is not a protocol implementation. Driver projects may
reference abstractions, protocols, and transports as needed, but not runtime, plugin
host, adapters, servers, or user-interface projects.

The separate
[declarative example plugin](../samples/Rig2Cast.DeclarativeExamplePlugin/README.md)
demonstrates all frozen version-1 typed descriptors, capability generation, and the
boundary between declarative data and driver/protocol behavior.

The first physical target is Yaesu FTDX10. A deterministic simulator precedes hardware integration so scheduling, leases, parsing, timeouts, disconnections, and unsolicited events can be tested repeatably.

## Communication failure classification

A driver or protocol layer must throw `RadioConnectionException` when a transport
failure or terminal protocol-session failure means that the current driver instance
cannot safely process another command. This marker asks the managed runtime to fault
the connection and start supervised recovery.

Do not use that exception for a syntactically invalid request, unsupported feature,
radio command rejection, or an isolated malformed response when the protocol session
can continue safely. Those errors are returned to the requesting client without
disconnecting every other client. A protocol response timeout may be returned as a
`TimeoutException` to the initiating caller, but if it makes late-response routing
ambiguous, the protocol must also terminate its observation stream with a
`RadioConnectionException` so the runtime replaces the session.
