# Capability model

Clients must be able to build generic user interfaces without model-specific knowledge. The model separates:

- **Capabilities:** what the physical model and current driver implementation support.
- **Availability:** what can be used in the current radio configuration.
- **State:** what the radio is doing now.
- **Authorization:** what the current client may do.

Support is not a Boolean. A feature can be unsupported by the radio, supported and implemented, known but not implemented by the driver, experimental, or unknown. Descriptors also report readable/writable access, valid targets, ranges, steps, and lease requirements.

Choice options may declare their applicable operating modes. For example, the FTDX10 `SH` command reuses the same code for different bandwidths in SSB and CW-family modes. Rig2Cast exposes stable bandwidth values such as `3000hz` and mode applicability, never the ambiguous CAT code.

Adapters translate requested passbands using these native choice capabilities.
An exact width is used when available; otherwise the closest writable width valid
for the requested mode is selected (the lower width wins an exact tie). The
`default` option represents the radio's native mode default.

Capabilities normally remain stable for a connection, but firmware, options, probing, or configuration can change them. Capability, availability, state, authorization, and lease changes are versioned and emitted as events. A client that misses revisions requests a complete snapshot.

Common capabilities are strongly typed. Manufacturer-specific features use versioned, namespaced extension descriptors that generic clients can safely ignore.

State snapshots are cached so multiple clients can inspect them without generating CAT traffic. Clients can request cached, bounded-age, or forced-refresh consistency. Concurrent bounded-age reads share one scheduled hardware refresh when the cache is stale; `RefreshStateAsync` remains the explicit force-refresh operation. Recognized unsolicited messages update both state and the freshness of the specific components they describe.
