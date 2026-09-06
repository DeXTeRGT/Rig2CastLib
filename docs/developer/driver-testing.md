# Driver test and release checklist

This checklist is the minimum evidence expected for a new or materially changed
driver. Hardware availability affects the validation label, not the need for
deterministic tests and honest documentation.

## Protocol and codec tests

- Golden encode/decode vectors copied from an identified official manual revision.
- Minimum, maximum, boundary, malformed, truncated, rejection, and unknown frames.
- Frequency representation, byte order, BCD/binary fields, mode mappings, and
  address direction.
- Echo handling, acknowledgements, negative acknowledgements, unsolicited frames,
  fragmented reads, concatenated frames, and noise before framing.
- Query correlation and late/identical response behavior.
- Caller cancellation, internal shutdown, response timeout, and disposal races.

## Deterministic simulator tests

- Identity/open success and every supported read/write command.
- Unsupported and firmware/option-dependent behavior.
- State read returns coherent VFO, receiver, RX/TX, split, mode, and timestamps.
- Setter acknowledgement/readback semantics.
- Transceive/automatic-information updates and ambiguous-change refresh requests.
- Disconnect during read/write, reconnect with a fresh instance, and recovery.
- Fake `TimeProvider` proves observation timestamps and timeout behavior.

The simulator must model documented radio behavior, including inconvenient
behavior. It must not merely return whatever makes the driver pass.

## Capability conformance

Add the driver to `DriverConformanceTests` using its deterministic scripted or
simulator-backed fixture. Run the shared `DriverConformance.AssertCapabilities`
and `AssertState` checks so every built-in driver is held to the same metadata,
interface, target, and topology rules. Use `AssertCoreMutationsAsync` with safe
fixture values to exercise advertised frequency, active-mode, and split writes;
scripted transports must verify exact CAT commands, while stateful simulators also
verify readback. Use `AssertFeatureMutationsAsync` for representative numeric,
switch, choice, and passband operations. The scenario value must satisfy its
advertised descriptor before the helper invokes the driver. Keep model-specific
command and boundary tests alongside these common checks; the conformance helper
does not replace them.

Use `AssertFeatureReadsAsync` to verify representative returned IDs, descriptor
ranges and options, normalized meter bounds, and mode-specific passband constraints.
Use `AssertRejectsInvalidCoreMutationsAsync` to cover undeclared persistent VFO
targets, out-of-range frequencies, and unsupported modes. A context query needed to
resolve a relative protocol selector is acceptable, but an invalid mutation must
not emit its write command.

Use `AssertDriverOwnsTransportAsync` and
`AssertUnknownModelDisposesTransportAsync` for every built-in factory. They verify
successful ownership transfer, idempotent driver disposal, and failed-validation
cleanup. Keep topology-specific targeted and receiver command sequences in the
model fixture while the common metadata checks ensure every advertised target has
the corresponding interface.

Run fault and cancellation conformance once per shared protocol engine, then add a
driver-level case only for model-specific probing, framing, or recovery behavior.
Every model does not need a duplicate of the same ASCII-session or CI-V-session
timeout test.

For every advertised feature:

- Access flags match the implemented interface and command direction.
- VFO and receiver target sets match operational overloads.
- Ranges, steps, options, modes, and units match the source.
- Mode applicability prevents/advises exactly the documented invalid modes.
- Option/firmware-dependent features appear only when present.
- Raw values are labeled raw; engineering units have evidence.
- Unsupported features fail without sending a misleading command.

Also test that every implemented common feature is advertised. Generic hosts must
not need a model-ID special case.

## Runtime integration

- Multiple sessions cannot interleave physical CAT exchanges.
- Observer writes are rejected; operator controls and controller lease operations
  follow authorization rules.
- Related mutations work under `ExecuteExclusiveAsync`.
- PTT requires a bounded transmit lease, supports renewal, returns to RX on release
  or expiry, and reports uncertain cleanup.
- Enforce and Advisory mode-applicability policies behave distinctly.
- Cached/fresh/forced state reads and event-driven state updates remain coherent.
- Terminal `RadioConnectionException` faults/reconnects; request errors do not.

## Factory, transport, and plugin tests

- Descriptor IDs are stable and unique; defaults belong to advertised ranges.
- Typed connection defaults, explicit values, application defaults, overrides,
  invalid values, and unknown IDs are covered.
- Serial framing is declared and enforced; raw TCP carries identical CAT bytes.
- The driver owns and disposes the transport after successful open.
- Every failed open path disposes the transport.
- Reconnect callbacks create fresh transport and driver instances.
- External manifest metadata exactly matches the factory; trusted hash succeeds,
  tampering fails, and development-mode behavior is visibly warned.

## Physical hardware pass

Record radio model, firmware, interface type, baud/framing, controller/radio
address, cabling, and test date. Then verify:

- Cold open, repeated open/close, radio-off, unplug, and reconnect.
- All non-destructive reads in every relevant operating mode and VFO arrangement.
- VFO A/B identity, foreground/background behavior, split RX/TX routing, and
  front-panel changes.
- Writes at safe frequencies and power; invalid-mode command behavior.
- Automatic-information/transceive behavior both enabled and disabled.
- PTT only into a dummy load at low power, including lease expiry and disconnect.
- Raw control/meter values at several physical settings without claiming an
  unsupported calibration curve.

Mark unperformed items as simulator-only or awaiting hardware. Never promote a
manual/simulator result to “physically validated.”

## Documentation and release gate

- Add/update the protocol-source record and coverage matrix.
- Add model-specific console commands and safety notes to the operating manual.
- Update the developer-facing model/settings information if contracts changed.
- Update `docs/AI_HANDOVER.md` with completed work, remaining work, quirks, and
  exact validation status.
- Run the full solution build/test suite and `git diff --check`.
- Review the diff for unrelated files, secrets, generated binaries, local ports,
  and machine-specific paths.
- Do not commit or publish until the maintainer has reviewed hardware-affecting
  behavior and requested that action.
