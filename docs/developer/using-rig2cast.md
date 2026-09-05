# Using Rig2Cast in an application

This guide describes the recommended native C# integration. Applications should
normally use `ManagedRadio` and `IRadioSession`, rather than calling a physical
driver directly. The runtime serializes CAT operations, maintains state,
authorizes clients, publishes events, supervises reconnects, and applies transmit
safety.

## 1. Reference and register drivers

Reference `Rig2Cast.Abstractions`, `Rig2Cast.Core`, `Rig2Cast.Runtime`, and the
transport and driver projects you ship. Register built-in factories explicitly:

```csharp
using Rig2Cast.Core.Drivers;
using Rig2Cast.Drivers.Elecraft.K3Family;
using Rig2Cast.Drivers.Icom.Ic7300;
using Rig2Cast.Drivers.Xiegu.G90;
using Rig2Cast.Drivers.Yaesu.Ftdx10;

var catalog = new RadioDriverCatalog();
catalog.Register(new Ftdx10DriverFactory());
catalog.Register(new ElecraftK3DriverFactory());
catalog.Register(new Ic7300DriverFactory());
catalog.Register(new G90DriverFactory());

foreach (RadioModelRegistration item in catalog.Models)
    Console.WriteLine($"{item.Model.Id}: {item.Model.Manufacturer} {item.Model.Model}");
```

Model IDs are stable, case-insensitive strings. The selected registration gives
the application the model descriptor, driver descriptor, and factory without
opening hardware.

External factories can be added through the trusted plugin host. See the
[plugin host guide](../plugin-host.md); development mode deliberately bypasses
hash verification and must not be used as a production trust policy.

## 2. Render and resolve model connection settings

`RadioModelDescriptor` advertises transports, baud rates, serial framing, and
typed model-specific settings. A generic UI should render
`model.ConnectionSettings` instead of checking model IDs. Values remain strings
at the UI/configuration boundary and are validated once:

```csharp
RadioModelRegistration registration = catalog.Find("icom.ic-7300");

var userValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["icom.civAddress"] = "94",       // hexadecimal because the definition says so
    ["icom.controllerAddress"] = "E0"
};

ResolvedConnectionSettings resolved = ConnectionSettingsResolver.Resolve(
    registration.Model,
    explicitValues: userValues);
```

Resolution precedence is explicit value, application default, then model
default. An application may pass `applicationDefaults` or safe
`definitionOverrides`; an override cannot change a setting's value type. Unknown,
missing required, malformed, out-of-range, and invalid-choice values fail before
the port is opened.

Pass the same textual values and the resolved result to the factory:

```csharp
var connection = new RadioConnectionOptions(
    "station-radio-1", registration.Model.Id, userValues)
{
    ResolvedSettings = resolved
};
```

Factories also resolve settings themselves when `ResolvedSettings` is absent,
which preserves compatibility with simpler callers.

## 3. Discover and open a transport

### Serial

Serial-port discovery is cross-platform and presentation-neutral:

```csharp
using Rig2Cast.Transports.Serial;

var discovery = new SystemSerialPortDiscovery();
foreach (var port in discovery.GetPorts())
    Console.WriteLine(port.DisplayName);
```

Create serial settings from model defaults, optionally selecting an advertised
baud rate:

```csharp
SerialConnectionSettings serial = SerialConnectionSettings.FromModel(
    registration.Model, "COM16", baudRate: 19_200);

IRadioTransport transport = SerialRadioTransportFactory.Create(
    registration.Model, serial);
```

On Linux, the port name is typically such as `/dev/ttyUSB0` or `/dev/ttyACM0`,
and the process needs OS permission to open it. Rig2Cast itself has no Windows UI
dependency. Leave `allowUnsafeOverride` false unless a deliberate diagnostic
tool must bypass model serial constraints.

### Transparent raw TCP

Raw TCP carries the CAT bytes unchanged. It is suitable for a serial-device
server or VSPE raw TCP endpoint; it is not Telnet, RFC2217, or `rigctld`:

```csharp
using Rig2Cast.Transports.Tcp;

IRadioTransport transport = new TcpRadioTransport(new TcpRadioTransportOptions
{
    Host = "127.0.0.1",
    Port = 5555,
    ConnectTimeout = TimeSpan.FromSeconds(10),
    NoDelay = true,
    KeepAlive = true
});
```

Only one component may read a CAT byte stream. Do not attach a second parser to
the same serial/TCP connection.

## 4. Create a reconnectable managed radio

For physical or network connections, use a connector that creates a fresh
transport and driver for every attempt:

```csharp
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Security;
using Rig2Cast.Abstractions.Sessions;
using Rig2Cast.Runtime.Sessions;

await using ManagedRadio radio = await ManagedRadio.CreateReconnectableAsync(
    "station-radio-1",
    async cancellationToken =>
    {
        IRadioTransport nextTransport = SerialRadioTransportFactory.Create(
            registration.Model, serial);
        return await registration.Factory.OpenAsync(
            connection, nextTransport, cancellationToken);
    },
    new ManagedRadioOptions
    {
        ModeApplicabilityPolicy = ModeApplicabilityPolicy.Enforce
    });

await using IRadioSession session = radio.OpenSession(
    new ClientIdentity("desktop-ui", "Station desktop"),
    ClientRole.Operator);
```

`CreateAsync` is available when reconnection is intentionally not required.
Successful factory open transfers transport ownership to the driver. Never reuse
a failed/disposed transport in a reconnect callback.

Roles are ordered as observer, operator, controller, and administrator. Observer
sessions read; operator-or-higher sessions can perform ordinary controls;
controller-or-higher sessions can manage exclusive-control leases. Always assign
the least privilege needed by a client.

## 5. Build the UI from a snapshot and capabilities

```csharp
RadioSnapshot snapshot = await session.GetSnapshotAsync();
RadioCapabilities caps = snapshot.Capabilities;

bool canWriteFrequency =
    caps.Frequency.Feature.Support == CapabilitySupport.Supported &&
    caps.Frequency.Feature.Access.HasFlag(FeatureAccess.Write);

foreach (VfoId vfo in caps.Vfos.Available) { /* create a VFO control */ }
foreach (var pair in caps.Controls)         { /* numeric control */ }
foreach (var pair in caps.Switches)         { /* check box */ }
foreach (var pair in caps.Choices)          { /* choice selector */ }
foreach (var pair in caps.Meters)           { /* read-only meter */ }
```

Treat the connected driver's `RadioCapabilities` as authoritative. Static model
metadata describes how to connect; runtime capabilities describe what this radio
instance can do. Check `FeatureDescriptor`, target sets, ranges, options, and
receiver topology before presenting or invoking an operation.

Numeric control and meter values may be manufacturer raw values. Units and
documented ranges are in their descriptors; do not assume a raw value means watts,
S-units, or percent. Calibration and presentation conversions belong in the
application unless a driver explicitly advertises engineering units.

Mode applicability metadata tells a UI which controls/options are valid in the
current mode. `ModeApplicabilityPolicy.Enforce` makes the runtime reject invalid
reads/writes before sending CAT. `Advisory` exposes the metadata but leaves the
decision to the application. Select this policy before connection and keep it
fixed for that managed-radio lifetime.

## 6. Read state and perform operations

```csharp
RadioState state = await session.ReadStateAsync(
    RadioReadRequest.FreshWithin(TimeSpan.FromMilliseconds(500)));

if (canWriteFrequency)
    await session.SetFrequencyAsync(VfoId.A, 14_200_000);

if (caps.Modes.Feature.Access.HasFlag(FeatureAccess.Write) &&
    caps.Modes.Values.Contains(RadioMode.Usb))
    await session.SetModeAsync(RadioMode.Usb);
```

Use the overload matching the advertised target: unqualified, `VfoId`, or
`ReceiverId`. A VFO is stored tuning state; a receiver is a physical/logical
signal path. They are not interchangeable. See the [receiver/VFO
model](../architecture/receiver-vfo-model.md).

`GetSnapshotAsync` is cached and includes capabilities, state, authorization,
availability, and leases. `RefreshStateAsync` forces hardware I/O.
`ReadStateAsync` lets the caller choose cached, fresh-with-maximum-age, or forced
behavior; avoid aggressive polling when transceive/automatic-information events
can maintain state.

Use `ExecuteExclusiveAsync` for a related sequence that must not be interleaved:

```csharp
await session.ExecuteExclusiveAsync(async (radioOps, ct) =>
{
    await radioOps.SetModeAsync(RadioMode.Cw, ct);
    await radioOps.SetPassbandAsync(500, ct);
});
```

## 7. Observe changes

```csharp
await foreach (RadioEvent radioEvent in session.WatchEventsAsync(stoppingToken))
{
    // Dispatch onto the UI thread if required, then refresh only affected views.
    Console.WriteLine(radioEvent);
}
```

Events are typed runtime state/availability/control notifications, not a public
copy of every incoming wire frame. A delivery gap or reconnect should prompt the
application to obtain a fresh snapshot. The future raw-frame fan-out/spectrum
stream must not be assumed to exist today.

## 8. PTT safety

PTT is deliberately not an ordinary boolean setter. It requires authorization
and a bounded transmit lease:

```csharp
LeaseToken tx = await session.AcquireLeaseAsync(
    LeaseKinds.Transmit, TimeSpan.FromSeconds(10));
try
{
    await session.SetPttAsync(true, tx);
    // Renew the lease before expiry only while transmission is intentionally active.
}
finally
{
    try { await session.SetPttAsync(false, tx); } catch { /* log safety failure */ }
    try { await session.ReleaseLeaseAsync(tx); } catch { /* log cleanup failure */ }
}
```

Use a dummy load and low power for physical tests. An application must surface
lease loss, connection loss, forced-RX cleanup failure, and uncertain transmit
state prominently; never silently assume RX after a failed CAT exchange.

## 9. Errors, cancellation, and disposal

- Pass cancellation tokens through every long-running operation.
- Treat `NotSupportedException` as a capability/caller mismatch, not a reconnect
  request.
- Treat validation exceptions as input errors.
- A terminal transport/protocol failure becomes `RadioConnectionException`; a
  reconnectable managed radio transitions availability and creates a new driver.
- Dispose sessions, then `ManagedRadio`. Driver disposal closes its transport.
- Do not issue parallel commands directly against a driver. The managed runtime
  is the serialization boundary.

For a complete capability-generated application, read the
[`Rig2Cast.CapabilityGui` guide](../../samples/Rig2Cast.CapabilityGui/README.md).
