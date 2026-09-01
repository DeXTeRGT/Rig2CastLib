# Capability model

Clients must be able to build generic user interfaces without model-specific knowledge. The model separates:

- **Capabilities:** what the physical model and current driver implementation support.
- **Availability:** what can be used in the current radio configuration.
- **State:** what the radio is doing now.
- **Authorization:** what the current client may do.

Support is not a Boolean. A feature can be unsupported by the radio, supported and implemented, known but not implemented by the driver, experimental, or unknown. Descriptors also report readable/writable access, valid targets, ranges, steps, and lease requirements.

Passband capability is strongly typed and mode-dependent. A constraint can publish
a discrete list, as required by the FTDX10 `SH` command, or a numeric minimum,
maximum, and step with a flag indicating that the radio may quantize the request,
as required by Elecraft `BW`. Applications therefore do not need to interpret
manufacturer CAT codes or invent a finite list for a continuous control.

Adapters translate requested passbands through this shared capability. The closest
valid discrete width is selected for a discrete radio; numeric widths are validated
and aligned to the advertised step. Mode and passband changes can execute in one
exclusive runtime operation.

Capabilities normally remain stable for a connection, but firmware, options, probing, or configuration can change them. Capability, availability, state, authorization, and lease changes are versioned and emitted as events. A client that misses revisions requests a complete snapshot.

Common capabilities are strongly typed. Manufacturer-specific features use versioned, namespaced extension descriptors that generic clients can safely ignore.

State snapshots are cached so multiple clients can inspect them without generating CAT traffic. Clients can request cached, bounded-age, or forced-refresh consistency. Concurrent bounded-age reads share one scheduled hardware refresh when the cache is stale; `RefreshStateAsync` remains the explicit force-refresh operation. Recognized unsolicited messages update both state and the freshness of the specific components they describe.
