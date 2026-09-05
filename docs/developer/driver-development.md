# Creating a Rig2Cast radio driver

A driver translates the common Rig2Cast radio model into one manufacturer's CAT
behavior. Keep byte framing/correlation in a protocol layer, model quirks and
capabilities in the driver, and multi-client policy in the runtime.

The smallest implementation consists of an `IRadioDriverFactory`, an
`IRadioDriver`, truthful `RadioCapabilities`, deterministic tests, and protocol
source documentation.

## 1. Choose the implementation boundary

Before coding, record the official manual revision, firmware assumptions, radio
address/default serial settings, and every command to be implemented. Manufacturer
documentation is authoritative. Hamlib is useful corroboration and a compatibility
reference, but it is not a substitute for protocol provenance or hardware tests.

Reuse a protocol engine when the wire family matches:

- `Rig2Cast.Protocols.Ascii` for correlated semicolon-framed ASCII CAT.
- `Rig2Cast.Protocols.Civ` for binary CI-V framing, addressing, echo handling,
  acknowledgement, and response correlation.
- `Rig2Cast.Protocols.Declarative` for compiled C# mappings, fields, queries,
  mode-dependent values, and conditional values.

Do not put serial-port code in a driver or runtime/session policy in a protocol
codec. A future legacy Yaesu binary driver should add/reuse a binary protocol
layer while retaining the same driver contracts.

The declarative vocabulary reduces repetitive mappings; it does not replace
imperative code for negotiation, state correlation, quirks, retries, or lifecycle.
See the [declarative guide](../declarative-engine.md).

## 2. Declare a factory and models

Implement `IRadioDriverFactory.Descriptor` with stable, namespaced IDs:

```csharp
public sealed class MyRadioDriverFactory : IRadioDriverFactory
{
    public const string ModelId = "vendor.model";

    public RadioDriverDescriptor Descriptor { get; } = new(
        "rig2cast.drivers.vendor.model",
        new Version(0, 1, 0),
        new Version(1, 0),
        [new RadioModelDescriptor(
            ModelId,
            "Vendor",
            "Model",
            new HashSet<RadioTransportKind>
                { RadioTransportKind.Serial, RadioTransportKind.Tcp },
            [9_600, 19_200],
            DefaultBaudRate: 19_200)
        {
            SerialProfile = SerialConnectionProfile.Create(
                dataBits: 8,
                parity: RadioSerialParity.None,
                stopBits: RadioSerialStopBits.One),
            ConnectionSettings =
            [
                new ConnectionSettingDefinition(
                    "vendor.address",
                    ConnectionSettingValueType.Byte,
                    "Radio address",
                    "CAT bus address in hexadecimal.",
                    DefaultValue: "70",
                    Format: ConnectionSettingFormat.Hexadecimal,
                    Minimum: 0,
                    Maximum: 255)
            ]
        }]);
```

Static descriptors must be usable before connecting. Put hardware/option/firmware
dependent information in the opened driver's capabilities, not in catalog guesses.
One factory may advertise multiple closely related models when their implementation
is genuinely shared.

## 3. Open safely and transfer ownership

Validate the model ID and resolve typed settings. The factory contract transfers
transport ownership to the driver; on every failed open path, dispose it:

```csharp
public async ValueTask<IRadioDriver> OpenAsync(
    RadioConnectionOptions options,
    IRadioTransport transport,
    CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(transport);
    RadioModelDescriptor model = Descriptor.Models.Single(
        candidate => StringComparer.OrdinalIgnoreCase.Equals(
            candidate.Id, options.ModelId));
    ResolvedConnectionSettings settings =
        ConnectionSettingsResolver.ResolveForFactory(options, model);

    try
    {
        if (!transport.IsConnected)
            await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);

        byte address = settings.Get<byte>("vendor.address");
        // Create the protocol session, perform a non-destructive identity probe,
        // and construct capabilities from the verified model/options.
        return new MyRadioDriver(transport, address, TimeProvider.System);
    }
    catch
    {
        await transport.DisposeAsync().ConfigureAwait(false);
        throw;
    }
}
```

Prefer a `TimeProvider` parameter internally and default it to
`TimeProvider.System`; tests can then make observation timestamps and timeout
logic deterministic.

An identity probe should be non-destructive. If firmware legitimately lacks an
ID command, use a documented safe fallback and advertise whether identity was
verified in namespaced capability extensions. A timeout must not crash the host
process; return a classified exception and leave ownership/disposal consistent.

## 4. Implement the common driver contract

Every driver implements `IRadioDriver`:

- `Capabilities`
- `ReadStateAsync`
- frequency, active-VFO, mode, split, and PTT setters
- `DisposeAsync`

Unsupported mandatory methods should return/throw `NotSupportedException`; their
capabilities must say unsupported. Do not silently ignore a requested mutation.
Validate command inputs before writing and, where the protocol allows it, require
acknowledgement and/or verified readback.

Implement optional interfaces only for features that exist:

- `IRadioControlDriver`, `IRadioSwitchDriver`, `IRadioChoiceDriver`
- `IRadioPassbandDriver`, `IRadioMeterDriver`
- VFO-targeted variants for controls, choices, passband, and meters
- receiver-targeted variants for frequency, mode, controls, switches, choices,
  passband, and meters
- `IRadioObservationSource` for unsolicited/transceive frames

The runtime routes an operation to these interfaces after checking capabilities.
Interface implementation and advertised support must agree.

## 5. Model state correctly

Populate both the compatibility fields and the explicit topology in `RadioState`:

- `Vfos`: frequency, mode, and timestamp per stored VFO.
- `Receivers`: current state per actual receive path.
- `ReceivePaths`: receiver-to-VFO routing.
- `TransmitPath` and `TransmitReceiver`: TX routing.
- `ActiveVfo`, `TransmitVfo`, split, PTT, connection status, and observation time.

Do not assume “foreground” means VFO A or that a mode response identifies a VFO
unless the protocol guarantees it. Correlate all necessary commands—for example,
some radios need separate state-information, opposite-VFO, and VFO-selection
responses. When the wire report is ambiguous, request a full state refresh rather
than publishing confidently wrong state.

See [receiver and VFO modeling](../architecture/receiver-vfo-model.md).

## 6. Advertise truthful capabilities

`RadioCapabilities` is an executable contract with generic applications. Declare:

- VFOs, selection and split access.
- Frequency targets, ranges, RX/TX legality, and smallest step when known.
- Modes and receiver-specific mode sets.
- PTT support/access.
- Numeric controls, switches, choices, passband, meters, and their targets.
- Receiver topology.
- Namespaced extensions only for genuinely vendor-specific information.

Use `FeatureDescriptor` access flags accurately. A read-only background VFO must
not be advertised writable; a write-only operation must not be presented as
readable. Capabilities may depend on the verified model, firmware, or installed
options.

For mode-dependent operations, set `ModeApplicabilityDescriptor` on the control,
switch, choice, passband, or meter descriptor. Choice options can additionally
carry applicable modes. This metadata is reusable by any UI, and applications can
choose runtime enforcement or advisory behavior. Avoid hard-coded UI rules.

Raw radio values are acceptable and often preferable. Give them honest bounds and
the unit `raw`; do not invent linear percent/watt/S-unit calibration when the
manual or measurements do not support it.

## 7. Publish unsolicited observations

Implement `IRadioObservationSource` when the protocol reports front-panel or
transceive changes. Decode frames once in the protocol/driver and publish the most
specific observation available: frequency, qualified state information, active
VFO, split, transmit, receiver routing, or a typed control change.

Use `StateRefreshRequestedObservation` when a valid report proves that state
changed but does not safely identify the complete new state. Use ignored versus
unknown observations deliberately, and report delivery gaps. Never let an
unsolicited frame steal a solicited query response; correlation belongs in the
single protocol session that owns the input stream.

Observations are semantic state input to the runtime. They are not the planned
public raw-frame fan-out API.

## 8. Classify failures

Throw `RadioConnectionException` when the current driver/protocol session is no
longer safe for subsequent commands, such as transport loss or irrecoverably
ambiguous response correlation. This triggers managed recovery.

Do not use it for invalid arguments, unsupported features, explicit radio command
rejection, or an isolated malformed response when the session remains usable.
A timeout can be returned to its caller, but if a late reply makes future
correlation unsafe, also terminate the observation/session path with
`RadioConnectionException`.

Caller cancellation must remain caller cancellation. Internal session shutdown
must not leak as an unrelated bare `OperationCanceledException`.

## 9. Concurrency, safety, and disposal

- Assume one reader and one serialized writer per physical CAT stream.
- Keep query correlation inside the protocol session.
- Never start independent driver polling loops that bypass runtime scheduling.
- PTT setters must represent actual radio state and errors honestly; the managed
  runtime supplies authorization and leases.
- Do not expose destructive operations such as VFO equalize/exchange without an
  explicit common contract and safety decision.
- Make disposal idempotent, stop observation work, and dispose the owned transport.
- Never swallow a failure that leaves TX state uncertain.

## 10. Register or package the driver

A built-in host registers the factory directly with `RadioDriverCatalog`. An
external driver references the allowed SDK layers, exports a public factory, and
ships a strict sidecar manifest whose metadata matches the factory exactly. API
compatibility is currently exact `1.0`; do not infer forward/backward compatibility.

Follow the [plugin host guide](../plugin-host.md) and start from the
[reference plugin](../../samples/Rig2Cast.ExamplePlugin/README.md). Driver projects
may reference abstractions, protocol, and transport layers as necessary, but
should not depend on runtime, plugin host, adapters, servers, or UI projects.

## 11. Document support

For each model, maintain:

- Official manual title/revision and command provenance.
- Firmware and option assumptions.
- Command/feature coverage matrix.
- Simulator status and physical validation status.
- Known quirks, raw-value semantics, safe fallbacks, and unsupported commands.
- Reproducible console/hardware test steps.

Then complete the [driver test and release checklist](driver-testing.md).

