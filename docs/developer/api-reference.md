# Native API reference map

This page maps the current public API by responsibility. It complements the
[application tutorial](using-rig2cast.md) and [driver tutorial](driver-development.md).
The C# declarations remain authoritative while the API is in early development.

## `Rig2Cast.Abstractions.Drivers`

| Type | Purpose |
| --- | --- |
| `IRadioDriverFactory` | Advertises driver/models and opens an `IRadioDriver` over an owned transport |
| `IRadioDriver` | Required state and core mutation contract implemented by every driver |
| `RadioDriverDescriptor` | Driver ID, implementation version, external API version, and models |
| `RadioModelDescriptor` | Stable model ID, display metadata, transports, baud rates, serial profile, and connection settings |
| `RadioConnectionOptions` | Radio/model IDs, textual settings, and optional pre-resolved typed settings |
| `ConnectionSettingDefinition` | Type, display text, requirement, default, format, range, and choices for one setting |
| `ConnectionSettingsResolver` | Validates overrides/defaults and creates `ResolvedConnectionSettings` |
| `SerialConnectionProfile` | Model-authoritative serial framing and configurable/fixed fields |
| `SerialConnectionSettings` | Concrete port, baud, framing, line, and timeout values |
| `IRadioControlDriver` | Unqualified numeric control reads/writes |
| `IRadioSwitchDriver` | Unqualified boolean feature reads/writes |
| `IRadioChoiceDriver` | Unqualified named-choice reads/writes |
| `IRadioPassbandDriver` | Unqualified passband reads/writes |
| `IRadioMeterDriver` | Unqualified meter reads |
| `IRadioTargeted*Driver` | VFO-targeted control, choice, passband, and meter variants |
| `IRadioReceiver*Driver` | Receiver-targeted frequency, mode, control, switch, choice, passband, and meter variants |
| `IRadioObservationSource` | Semantic unsolicited/transceive observation stream from a driver |
| `RadioDriverObservation` | Base for typed state/control, gap, ignored, unknown, and refresh-request observations |
| `RadioConnectionException` | Marks a terminal driver/session connection failure requiring replacement |

Factory ownership rule: after `OpenAsync` is called, the factory/driver is
responsible for disposing the supplied transport on both failure and eventual
driver disposal.

## `Rig2Cast.Abstractions.Capabilities`

| Type | Purpose |
| --- | --- |
| `RadioCapabilities` | Authoritative connected-instance feature contract |
| `FeatureDescriptor` | Supported/unsupported state, read/write access, optional required lease, and detail |
| `VfoCapability` | Available VFOs plus selection and split access |
| `FrequencyCapability` | Targets, ranges, RX/TX legality, optional step, and receiver-specific ranges |
| `ModeCapability` | Supported modes and receiver-specific mode sets |
| `ReceiverTopologyCapability` | Available receivers, VFO relationships, and receiver selection |
| `PassbandCapability` | Passband support, bounds, targets, and mode applicability |
| `ModeApplicabilityDescriptor` | Modes in which a feature can be read and/or written |
| `NumericControlDescriptor` | Numeric bounds, step, unit, access, applicability, and targets |
| `SwitchControlDescriptor` | Boolean access, applicability, and receiver targets |
| `ChoiceControlDescriptor` | Named options, applicability, VFO/receiver targets, and target-specific options |
| `RadioMeterDescriptor` | Raw/normalized meter metadata, targets, and applicability |

Applications should enumerate these descriptors. They should not infer support
from a model name or from the presence of a method on `IRadioSession`.

## `Rig2Cast.Abstractions.Radios`, `.Controls`, and `.Meters`

| Type | Purpose |
| --- | --- |
| `RadioState` | Coherent connection, VFO, receiver/path, mode, split, PTT, and timestamp state |
| `RadioVfoState` | Per-VFO frequency/mode state |
| `RadioReceiverState` | Per-receiver frequency/mode/passband and selected-VFO state |
| `RadioSignalPath` | Receiver-to-VFO routing |
| `RadioSnapshot` | Capabilities, availability, state, caller authorization, and leases |
| `VfoId`, `ReceiverId`, `RadioMode` | Shared typed identities/enumeration |
| `RadioControlId`, `RadioControlValue` | Numeric control identity and observed raw/value result |
| `RadioSwitchId`, `RadioSwitchValue` | Boolean control identity and result |
| `RadioChoiceId`, `RadioChoiceValue` | Named-choice identity and result |
| `RadioPassbandValue` | Observed passband result |
| `RadioMeterId`, `RadioMeterReading` | Meter identity and raw/normalized observation |

Use values and units exactly as advertised. `NormalizedValue` does not by itself
establish an engineering calibration.

## `Rig2Cast.Abstractions.Sessions`

`IRadioSession` is the recommended application API. Its method groups are:

- State: `GetSnapshotAsync`, `RefreshStateAsync`, and `ReadStateAsync`.
- Events: `WatchEventsAsync`.
- Core mutations: `SetFrequencyAsync`, `SetActiveVfoAsync`, `SetModeAsync`, and
  `SetSplitAsync`.
- Feature reads/writes: numeric controls, switches, choices, passband, and meters,
  including the applicable unqualified, VFO, and receiver overloads.
- Safety: acquire/renew/release leases and lease-protected `SetPttAsync`.
- Atomicity: `ExecuteExclusiveAsync` supplies an `IRadioOperationScope` for a
  non-interleaved mutation sequence.

`RadioReadRequest.Cached`, `RadioReadRequest.FreshWithin(maximumAge)`, and
`RadioReadRequest.ForceRefresh` select state consistency explicitly.

## `Rig2Cast.Abstractions.Security`

| Type | Purpose |
| --- | --- |
| `ClientIdentity` | Stable logical client identity and optional display name |
| `ClientRole` | Observer, operator, controller, or administrator authorization level |
| `ClientAuthorization` | Effective authorization exposed in a snapshot |
| `LeaseKinds` | Standard transmit and exclusive-control lease identifiers |
| `LeaseToken` | Owner-bound, kind-bound, expiring authority token |
| `LeaseUnavailableException`, `InvalidLeaseException` | Expected lease contention/validation failures |

## `Rig2Cast.Core.Drivers`

| Type | Purpose |
| --- | --- |
| `RadioDriverCatalog` | Registers factories, lists models, and resolves a model ID |
| `RadioModelRegistration` | Joins one model descriptor to its driver descriptor and factory |

Catalog registration rejects duplicate model IDs and inconsistent default baud
metadata.

## `Rig2Cast.Runtime.Sessions`

| Type | Purpose |
| --- | --- |
| `ManagedRadio` | Owns one active driver, scheduling, state, events, sessions, leases, and optional reconnect supervision |
| `ManagedRadioOptions` | Per-managed-radio policies, currently mode-applicability Enforce/Advisory |
| `RadioConnectionSupervisorOptions` | Reconnect timing/attempt behavior |
| `RadioDriverConnector` | Callback that constructs a fresh opened driver for initial/reconnect attempts |
| `RenewingTransmitController` | Helper for maintaining a bounded transmit lease while intentional TX continues |

Dispose `IRadioSession` instances and then the owning `ManagedRadio`.

## `Rig2Cast.Abstractions.Transports` and `Rig2Cast.Transports`

| Type | Purpose |
| --- | --- |
| `IRadioTransport` | Connect/disconnect/read/write abstraction owned by a driver |
| `ISerialPortDiscovery` | Presentation-neutral serial-port enumeration |
| `SystemSerialPortDiscovery` | `System.IO.Ports` implementation for Windows/Linux hosts |
| `SerialRadioTransportFactory` | Validates model framing and constructs serial transports |
| `TcpRadioTransport`, `TcpRadioTransportOptions` | Transparent raw CAT-over-TCP stream |

Transport selection belongs to the host. A driver consumes `IRadioTransport` and
must not care whether identical CAT bytes arrived from serial or raw TCP.

## Plugin and declarative APIs

`Rig2Cast.PluginHost` loads explicitly trusted external factories and validates
manifest/factory compatibility. Follow the [plugin host guide](../plugin-host.md).

`Rig2Cast.Protocols.Declarative` supplies immutable compiled descriptor types for
wire/value maps, numeric fields, ASCII queries, mode-dependent value sets, and
conditional values. Follow the [declarative engine guide](../declarative-engine.md)
and its [sample plugin](../../samples/Rig2Cast.DeclarativeExamplePlugin/README.md).

## Versioning note

The external driver API currently uses exact canonical version `1.0` matching.
The repository is in active early development and broader public source/API
compatibility is not yet guaranteed. Build against and test with a named release
or commit, and review changes to abstractions before upgrading.

