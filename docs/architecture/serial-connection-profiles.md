# Serial connection profiles

Serial configuration has two distinct inputs:

- `RadioModelDescriptor.SerialProfile` describes a model's defaults and constraints.
- `SerialConnectionSettings` contains the effective values selected by a host/user.

Hosts obtain initial values with:

```csharp
SerialConnectionSettings settings =
    SerialConnectionSettings.FromModel(model, portName, selectedBaudRate);
```

A GUI may edit the returned immutable record with `with`. Each `SerialSetting<T>` in
the profile tells the GUI its default, whether ordinary editing is permitted, and any
allowed values. Fixed settings may still be exposed under an explicit advanced or
unsafe-override workflow; the user's decision is passed only when constructing the
transport:

```csharp
IRadioTransport transport = SerialRadioTransportFactory.Create(
    model, settings, allowUnsafeOverride: advancedOverrideConfirmed);
```

The shared factory validates model transport support, baud rate, fixed/constrained
settings, data-bit bounds, and positive timeouts, then maps Rig2Cast-owned serial enums
to `System.IO.Ports`. Hosts must create a fresh transport from the saved effective
settings on every reconnect attempt.

Built-in model descriptors use typed profiles. `SerialConnectionProfile.Resolve`
retains compatibility with external plugins that still publish legacy `serial.*`
string defaults, falling back to standard 8-N-1 when those keys are absent. Typed
metadata is authoritative when present. The legacy keys can be removed only in a
future explicitly versioned plugin-contract migration.

Driver factories continue to accept an already-created `IRadioTransport`; drivers do
not select COM ports or construct physical transports. Protocol settings such as
`icom.civAddress` remain in `RadioConnectionOptions.Settings` and are independent of
serial framing.

The diagnostic Console exposes `--serial-data-bits`, `--serial-parity`,
`--serial-stop-bits`, `--serial-handshake`, `--serial-dtr`, `--serial-rts`, and
read/write timeout options. Every option falls back to the typed model default when
omitted. `--allow-unsafe-serial-overrides` is required for fixed framing or unsupported
baud values; the effective configuration is printed before connection.
