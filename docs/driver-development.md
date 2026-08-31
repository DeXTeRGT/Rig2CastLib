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

A plugin exposes metadata through a sidecar manifest so it can be discovered without loading its assembly. Loading requires an explicit trust record containing at least plugin ID and SHA-256 binary hash; development mode may opt out.

Drivers compose transport, framing/codec, manufacturer or protocol-family behavior, and model-specific capability/quirk declarations. Declarative descriptions are encouraged for regular commands, while exceptional behavior remains expressible in C#.

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
