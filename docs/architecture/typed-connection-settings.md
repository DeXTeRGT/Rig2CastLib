# Typed connection settings

`RadioModelDescriptor.ConnectionSettings` is the authoritative, discoverable schema
for model-specific settings that are needed before a driver opens. This is separate
from serial framing: CI-V addresses apply equally to serial and raw TCP transports.

Each `ConnectionSettingDefinition` supplies a stable identifier, value type, default,
required/optional state, display name, description, format hint, and optional numeric
range or choices. Built-in examples are `icom.civAddress`,
`icom.controllerAddress`, `yaesu.autoInformation`, and the Elecraft automatic-
information settings.

Hosts resolve settings before opening a transport or driver:

```csharp
ResolvedConnectionSettings settings = ConnectionSettingsResolver.Resolve(
    model,
    explicitValues: userValues,
    applicationDefaults: preferredDefaults,
    definitionOverrides: advancedDefinitionOverrides);
```

Precedence is explicit user value, application default, then model default. Required
values without any source fail validation. Parsing, declared ranges, choices, and
unknown IDs are also validated centrally. Each result reports its source and exposes
a strongly typed value through `Get<T>`.

Normal applications should override values, not definitions. Definition replacement
is intentionally explicit and supports controlled experimental firmware, clones, or
application policy. A host that passes replacement definitions owns that divergence.
It may attach the result to `RadioConnectionOptions.ResolvedSettings`; factories
verify the model identity and consume those typed results. If no resolved result is
provided, a factory resolves the legacy `Settings` dictionary itself. This preserves
compatibility for existing callers while keeping parsing out of built-in factories.

Legacy `DefaultConnectionSettings` keys remain temporarily for plugin/host
compatibility. Typed definitions are authoritative where present; removing the old
dictionary entries requires a versioned plugin-contract migration.

Plugin model manifests may publish the same optional definitions. The plugin host
validates their structure before loading and requires them to match the trusted
factory descriptor afterward. Older manifests that advertise none remain valid when
their corresponding factory advertises none.

## Serial-port discovery

`ISerialPortDiscovery` belongs to the transport abstraction and returns neutral
`SerialPortDescriptor` records. `SystemSerialPortDiscovery` implements it with
`System.IO.Ports.SerialPort.GetPortNames()`, removes duplicates, and naturally sorts
names such as `COM2` before `COM10`. It works with Windows names and Unix device
paths. Discovery is a snapshot; GUI applications decide when to refresh, how to
label controls, and whether to allow a manually entered device path.

The Console demonstrates both surfaces:

```powershell
Rig2Cast.Console.exe --list-ports
Rig2Cast.Console.exe --model icom.ic-7300 --list-connection-settings
Rig2Cast.Console.exe --model icom.ic-7300 --connection-setting icom.civAddress=70
```

The generic `--connection-setting <id=value>` option is repeatable. Existing
convenience switches remain available and feed the same resolver.
