 ## Executive assessment

  Rig2Cast has a strong core architecture for its current maturity. The separation between abstractions, protocol engines, transports, drivers, runtime, and adapters is fundamentally correct. The byte-oriented transport and serialized
  managed runtime give CI‑V and legacy Yaesu CAT a viable foundation.

  My assessment:

   Area                             Assessment
  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
   Overall architecture             Good
  ───────────────────────────────  ─────────────────────────────────────────────────────────────
   Concurrency model                Good, with cancellation caveat
  ───────────────────────────────  ─────────────────────────────────────────────────────────────
   Transmit safety                  Strong intent; a few failure-path weaknesses
  ───────────────────────────────  ─────────────────────────────────────────────────────────────
   Protocol reliability             Good for normal flows; one high-severity correlation defect
  ───────────────────────────────  ─────────────────────────────────────────────────────────────
   Capability model                 Promising but internally inconsistent in places
  ───────────────────────────────  ─────────────────────────────────────────────────────────────
   Multi-receiver extensibility     Directionally good, migration incomplete
  ───────────────────────────────  ─────────────────────────────────────────────────────────────
   CI‑V readiness                   Moderate
  ───────────────────────────────  ─────────────────────────────────────────────────────────────
   Legacy Yaesu binary readiness    Moderate
  ───────────────────────────────  ─────────────────────────────────────────────────────────────
   Plugin extensibility             Design only; not operational
  ───────────────────────────────  ─────────────────────────────────────────────────────────────
   Automated coverage               Good baseline, important gaps remain

  The suite passes: 139 passed, 0 failed.

  No source files were changed and no commit was made. The worktree was already dirty with the files described in the handover.

  ## Findings

  ### High — cancelling an in-flight ASCII query can corrupt later response correlation

  After a query has been written, caller cancellation exits the wait and clears _pending, but does not fault the protocol session. A late reply can then satisfy a later query using the same prefix.

  Relevant code:

  - The query waits on a token linked to caller cancellation at src/Rig2Cast.Protocols/Ascii/AsciiCatSession.cs:108.
  - Only timeout, not caller cancellation, faults the session at src/Rig2Cast.Protocols/Ascii/AsciiCatSession.cs:114.
  - The pending query is cleared and the transaction lock released at src/Rig2Cast.Protocols/Ascii/AsciiCatSession.cs:128.

  Example:

  1. Send FA;.
  2. Cancel after the complete frame is written.
  3. _pending is removed and the session remains usable.
  4. Start another FA; query.
  5. The response to the cancelled query arrives and may complete the new query.

  This violates the same correlation rule that correctly makes response timeout terminal.

  Recommendation: distinguish cancellation before write from cancellation after the write boundary. Cancellation before writing is harmless. Once a query frame has been committed, abandonment before a matching response must either:

  - fault and replace the session, which is the safest initial policy; or
  - continue draining the original response before releasing the transaction, an optimization that requires careful bounded semantics.

  Add a deterministic cancel → late response → identical-prefix query regression test.

  ### High — the lease safety monitor can terminate permanently on a de-key failure

  MonitorLeasesAsync only handles shutdown cancellation. If ForcePttOffAsync throws because of a transport failure, command timeout, readback failure, or driver error, the monitor task faults and never processes another lease expiration.

  See src/Rig2Cast.Runtime/Sessions/ManagedRadio.cs:934 and the unguarded forced RX call at src/Rig2Cast.Runtime/Sessions/ManagedRadio.cs:950.

  The command-failure supervisor may reconnect after a RadioConnectionException, but it does not restart the lease monitor. Other exception types may not trigger recovery at all.

  Recommendation:

  - Keep the monitor alive after individual failures.
  - Publish a prominent safety diagnostic.
  - Trigger connection recovery when the connection is unsafe.
  - Retry forced RX with a bounded policy.
  - On reconnection, if there is no valid transmit lease, force RX before declaring the replacement operational.
  - Add tests where the first expiry de-key fails and a later transmit lease still expires safely.

  ### High — disposal can abort before closing the driver and serial transport

  During disposal, forced PTT-off happens before scheduler and driver cleanup at src/Rig2Cast.Runtime/Sessions/ManagedRadio.cs:1521. If it throws, execution never reaches scheduler disposal or driver/transport disposal at src/
  Rig2Cast.Runtime/Sessions/ManagedRadio.cs:1537.

  That is especially undesirable when the serial connection is itself failing: the path most likely to make de-key fail is also the path where closing the port is essential.

  Recommendation: structure disposal as best-effort safety cleanup followed by unconditional resource cleanup in finally, preserving or aggregating the original safety exception. Disposal cannot guarantee that a physically disconnected
  radio leaves TX, but it must always exhaust all available mechanisms and close resources.

  ### Medium — legacy runtime operations do not consistently enforce capabilities

  Receiver-targeted frequency and mode operations validate targets and values, but legacy operations generally delegate directly:

  - Legacy frequency: src/Rig2Cast.Runtime/Sessions/ManagedRadio.cs:202
  - Legacy mode: src/Rig2Cast.Runtime/Sessions/ManagedRadio.cs:236
  - Legacy split: src/Rig2Cast.Runtime/Sessions/ManagedRadio.cs:260

  Drivers perform some validation, but the architectural claim that the runtime uniformly validates executable capabilities is not yet true. Different drivers can consequently expose different exception types and behavior for equivalent
  invalid requests.

  Recommendation: centralize validation for every public runtime operation. Driver validation should remain as defense in depth.

  ### Medium — target-aware mode applicability uses the global mode

  Targeted choice and passband validation uses _state.Mode:

  - src/Rig2Cast.Runtime/Sessions/ManagedRadio.cs:483
  - src/Rig2Cast.Runtime/Sessions/ManagedRadio.cs:556
  - src/Rig2Cast.Runtime/Sessions/ManagedRadio.cs:579

  For an independent sub-receiver, slice, or secondary VFO, its mode may differ from the selected/global mode. The runtime could reject a valid passband or send a mode-inappropriate control.

  This is likely to surface with more capable CI‑V radios before it surfaces on the current FTDX10/K3S set.

  Recommendation: resolve mode from the actual receiver or VFO state. If that mode is unavailable or stale, explicitly refresh or reject rather than borrowing the selected receiver’s mode.

  ### Medium — the core driver interface still makes A/B-style operations mandatory

  Every driver must implement VFO frequency, active-VFO selection, mode, split, and PTT on the root interface at src/Rig2Cast.Abstractions/Drivers/DriverContracts.cs:54.

  The synthetic multi-receiver proof reportedly implements these as unsupported stubs. That demonstrates the state model’s flexibility, but also shows that the driver contract has not completed the same migration.

  This will be awkward for:

  - CI‑V receivers without transmit support;
  - radios with more than A/B registers;
  - receiver-only devices;
  - slice-based network radios;
  - legacy Yaesu models without explicit split-VFO selection.

  Recommendation: for the next major API, reduce the mandatory driver interface to lifecycle, capabilities, and state. Move frequency, mode, split, PTT, and VFO selection into optional feature interfaces. Preserve the current API through
  adapters/default implementations during an additive transition.

  ### Medium — binary observation diagnostics are string-centric

  Every observation requires RawFrame as a string at src/Rig2Cast.Abstractions/Drivers/RadioDriverObservation.cs:22.

  For ASCII this is natural. For CI‑V and legacy Yaesu binary, it forces an arbitrary hex/Base64/text conversion into the generic driver contract.

  Recommendation: introduce an optional structured wire diagnostic, for example protocol name plus immutable byte payload and/or formatted representation. Do not expose mutable driver-owned arrays. Keep normal domain observations
  protocol-neutral.

  ### Low/medium — time abstraction is applied inconsistently

  ManagedRadio and RadioLeaseManager accept TimeProvider, but RenewingTransmitController compares leases against DateTimeOffset.UtcNow at src/Rig2Cast.Runtime/Sessions/RenewingTransmitController.cs:61. Reconnect timestamps also bypass
  the configured provider.

  This reduces deterministic testability and can create inconsistent expiry decisions when a non-system provider is used.

  Recommendation: use one injected TimeProvider across runtime components, including delay/timer creation.

  ### Low — plugin and server extensibility are currently scaffolding

  The plugin host currently consists of manifest/trust records at src/Rig2Cast.PluginHost/PluginManifest.cs:3. The server is a placeholder at src/Rig2Cast.Server/Program.cs:1.

  This is acceptable if communicated as future work, but external driver extensibility should not yet be described as implemented. Missing pieces include manifest validation, API compatibility rules, trust-store handling, safe path
  resolution, duplicate detection across assemblies, load contexts, diagnostics, and unload/lifetime behavior.

  ## What is already strong

  Several decisions are notably sound:

  - IRadioTransport is byte-oriented, so it does not constrain future binary protocols.
  - A single scheduler owns hardware traffic.
  - Connection-generation stamping prevents queued work from silently executing on a replacement radio.
  - Protocol-specific framing remains outside the generic domain abstractions.
  - ASCII timeouts correctly make the correlation session terminal.
  - Driver ownership of transports is documented.
  - Bounded subscriber queues make delivery loss explicit.
  - Receiver identity is extensible rather than another fixed enum.
  - Capabilities are instance-aware, allowing firmware and installed-option gating.
  - Transmit leases, short renewal windows, safety priority, session cleanup, and readback verification form a good safety model.
  - The simulator and scripted transports are the right testing strategy when physical hardware is unavailable.

  ## CI‑V readiness

  The generic runtime can support CI‑V without fundamental replacement. A separate CI‑V engine should sit beside AsciiCatSession; it should not extend or parameterize the ASCII engine.

  The engine will need:

  - FE FE synchronization and FD termination.
  - Controller and radio address handling.
  - Command/subcommand/data correlation.
  - FA negative acknowledgements and FB acknowledgements.
  - Optional serial echo suppression.
  - Transceive announcements interleaved with replies.
  - Recovery from noise, partial frames, repeated preambles, and malformed escapes/data.
  - Model-dependent BCD frequency and mode/filter fields.
  - Explicit policy for multiple CI‑V devices sharing a bus.
  - A response matcher stronger than “expected prefix.”
  - Address metadata that remains inside the CI‑V layer, except where a host explicitly needs connection configuration.

  I would model a CI‑V transaction key from source/destination plus command/subcommand and a command-specific validator. Because unsolicited transceive messages can resemble replies, fixtures must cover both orderings and identical-
  command announcements.

  CI‑V implementation ease today: approximately 6.5/10. Transport, runtime, leases, state, and capabilities are reusable. The principal friction points are mandatory legacy driver methods, string-only raw frames, and incomplete receiver-
  target semantics.

  ## Legacy Yaesu binary CAT readiness

  Legacy Yaesu binary CAT also deserves its own engine. It differs substantially from modern semicolon CAT:

  - commonly fixed five-byte commands;
  - model-specific opcode and byte layouts;
  - BCD frequency encoding;
  - commands that return fixed-length binary blocks;
  - commands with no response or weak acknowledgement;
  - required command pacing on some radios;
  - model-specific status bitfields;
  - no reliable universal unsolicited stream.

  The engine should support explicit transaction descriptions:

  - exact request bytes;
  - expected response length or framing rule;
  - acknowledgement policy;
  - minimum inter-command delay;
  - echo policy;
  - parser/validator;
  - whether timeout makes the session ambiguous or merely means “no acknowledgement.”

  For write-only operations, the driver/runtime must not claim confirmed state unless it can perform an independent readback. The current mutation pattern always reads full state afterward, which is safe if the radio supports it, but
  needs a defined outcome for write-only legacy models.

  Legacy Yaesu implementation ease today: approximately 7/10. It is somewhat simpler than CI‑V because there is usually no addressed multi-device bus, but model variation will require disciplined profiles rather than one universal Yaesu
  binary codec.

  ## Recommended plan

  1. Close the safety and correlation gaps first.

     Add post-write cancellation faulting, resilient lease monitoring, and unconditional disposal cleanup.

  2. Make runtime capability enforcement uniform.

     Validate support, access, target, range, mode applicability, and implementation availability through one reusable validation layer.

  3. Finish target-aware state semantics.

     Resolve passband and control applicability from the actual receiver/VFO mode. Define freshness for per-receiver frequency, mode, and passband rather than only legacy global components.

  4. Stabilize the driver SDK before loading third-party assemblies.

     Separate the minimal driver lifecycle interface from optional feature interfaces. Define API compatibility and immutable diagnostic contracts.

  5. Implement the CI‑V engine as the architectural stress test.

     Start with byte-level fixtures, echo, transceive interleaving, negative acknowledgements, malformed frames, cancellation boundaries, and reconnect behavior. Then add a simulated single-receiver Icom profile.

  6. Implement legacy Yaesu binary as a profile-driven engine.

     Keep framing and pacing generic, while command layouts, status blocks, and capability declarations live in immutable model profiles.

  7. Only then implement plugin loading.

     Validate manifests without loading assemblies, pin API compatibility, verify hashes, detect duplicate IDs, isolate load failures, and provide actionable diagnostics.

  8. Expand test organization.

     Retain end-to-end runtime tests, but add protocol-specific test projects for ASCII, CI‑V, and Yaesu binary. Include fuzz/property-style framing tests and deterministic shutdown/failure tests.

  Overall, the project does not need an architectural rewrite. It needs a targeted hardening pass and completion of the receiver-oriented contract migration before binary protocols and third-party drivers make today’s transitional
  inconsistencies expensive to change.
