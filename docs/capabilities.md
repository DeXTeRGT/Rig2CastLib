# Capability model

Clients must be able to build generic user interfaces without model-specific knowledge. The model separates:

- **Capabilities:** what the physical model and current driver implementation support.
- **Availability:** what can be used in the current radio configuration.
- **State:** what the radio is doing now.
- **Authorization:** what the current client may do.

Support is not a Boolean. A feature can be unsupported by the radio, supported and implemented, known but not implemented by the driver, experimental, or unknown. Descriptors also report readable/writable access, valid targets, ranges, steps, and lease requirements.

Choice options may declare their applicable operating modes. For example, the FTDX10 `SH` command reuses the same code for different bandwidths in SSB and CW-family modes. Rig2Cast exposes stable bandwidth values such as `3000hz` and mode applicability, never the ambiguous CAT code.

Capabilities normally remain stable for a connection, but firmware, options, probing, or configuration can change them. Capability, availability, state, authorization, and lease changes are versioned and emitted as events. A client that misses revisions requests a complete snapshot.

Common capabilities are strongly typed. Manufacturer-specific features use versioned, namespaced extension descriptors that generic clients can safely ignore.
