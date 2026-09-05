# Rig2Cast capability-driven GUI sample

This small Avalonia desktop application demonstrates how a host can build its user
interface without radio-model conditionals:

- model and transport selectors use `RadioDriverCatalog` metadata;
- serial ports use `ISerialPortDiscovery`;
- baud rates and serial framing come from the model descriptor;
- model-specific fields are generated from `ConnectionSettingDefinition`;
- inputs are centrally resolved and validated before connection;
- operational read/write controls are generated after connection from
  `RadioCapabilities`;
- meters and numeric controls show driver/raw values. Calibration and presentation
  conversion remain application responsibilities.

The application starts in read-only mode. Checking **Enable non-PTT writes** before
connecting opens an Operator session and enables only controls whose runtime
capabilities advertise write access. CAT PTT is intentionally absent from this sample
because a production UI must provide a complete transmit-lease and emergency-release
experience.

Run from the repository root:

```powershell
dotnet run --project samples\Rig2Cast.CapabilityGui\Rig2Cast.CapabilityGui.csproj
```

The FTDX10, IC-7300, and G90 can be exercised without hardware using Simulator.
The small sample does not include an Elecraft simulator peer; use serial or raw TCP
for Elecraft hardware.

In Serial mode the selected discovered port is used by default. The manual port field
is blank and disabled until **Override discovered port** is checked; this prevents a
stale manual value from silently taking precedence. Disconnect cleanup isolates and
reports session, managed-radio, and simulator disposal failures so changing from one
radio to another cannot terminate the UI through an unhandled event exception. Reused
transport editors are detached from their old row before the dynamic form creates a
replacement because Avalonia does not permit a control to have two visual parents.

After connecting, the application performs one serialized refresh of complete radio
state and every advertised readable numeric, switch, choice, and meter control. The
**Refresh all** button repeats this operation. A background session event watcher
applies transceive/automatic-information state events to VFO frequency, active VFO,
mode, split, and status controls without polling. Typed control events update their
corresponding generated editors as well. Controls are displayed in bordered VFO/core,
numeric, switch, choice, and meter groups.

The reference UI uses a fixed connection sidebar and a responsive radio workspace.
Operational content is separated into **Radio**, **Controls**, **Meters**, and
**Diagnostics** tabs so a model with a large capability surface does not become one
unstructured scrolling form. The header presents the active frequency, VFO, mode,
split, RX/TX, and connection status. Diagnostics are retained in a timestamped,
bounded 500-entry log instead of replacing the last useful message; clearing that log
is an application presentation action and does not affect the radio session.

Each advertised frequency target is rendered once as a VFO card in the workspace
header, with a formatted MHz readout and an exact-Hz editor. Active receive and split-transmit roles are highlighted
from `RadioState`; application-side step buttons use the driver's smallest advertised
step plus common larger increments and still call the normal checked session setter.
Extended editors show capability-derived access labels and tooltips for ranges, units,
steps, available options, and read/write support. Write actions consistently use the
term **Apply**, including write-only controls whose current value cannot be queried.

The Controls tab deliberately groups different descriptor kinds by operating purpose:
Audio, RF, Transmit, DSP, Filtering, CW, Operating, and Other. This classification is
owned by the sample and maps generic `RadioControlId`, `RadioSwitchId`, and
`RadioChoiceId` values; it never branches on manufacturer or model. Consequently a
new driver automatically receives the same presentation when it advertises existing
generic controls, while unknown future IDs have an explicit Other fallback.

Action-oriented controls use a shared five-column grid for label, editor, Read,
Apply, and access metadata, keeping Radio, Controls, and Meters aligned. Boolean
switches are deliberately different: connect-time refresh establishes their state and
a user toggle applies the new two-state value immediately, so redundant Read and Apply
buttons are omitted. Switches use a strict, aligned two-column tile grid, and
programmatic refresh/event changes are suppressed from echoing writes back to the rig.
Split uses the same immediate-toggle behavior and update guard. Other actions retain
explicit Read/Apply semantics in a shared grid with columns wide enough for complete
button labels.

Mode applicability is also capability-driven. The GUI disables protocol-invalid or
operationally irrelevant controls, excludes them from **Refresh all**, and performs a
one-time targeted refresh when a mode transition makes a control newly readable.
Tooltips list restricted read, write, and operational modes. These facts come from
driver descriptors; the GUI contains no model-specific mode table.

The connection-time **Mode restrictions** selector aligns presentation with the
runtime policy. **Enforce** disables and skips inapplicable controls and configures
`ManagedRadio` to reject those operations before CAT I/O. **Advisory** retains the
advertised metadata and tooltips but leaves controls enabled and includes them in
refresh/write operations. The selector is locked while connected so presentation and
runtime behavior cannot diverge during a session.

Physical serial and TCP sessions use `ManagedRadio.CreateReconnectableAsync` and
capture immutable connection values before starting recovery, so every retry creates
a fresh transport without accessing UI controls from a background thread. Diagnostic
events are displayed in the status area. If a bulk refresh fails, the failing control
name and error are retained and further reads stop once the connection is no longer
usable.
