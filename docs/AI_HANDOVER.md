# Rig2Cast AI development handover

Last updated: 2026-09-02 (Europe/Bucharest)

This document is the continuity source for an AI agent resuming development of
Rig2Cast. Read it before modifying code. Then read the architecture and decision
documents referenced below and inspect the current worktree; this file describes
the intended state but the code and tests remain authoritative.

## 1. Project objective and non-goals

Rig2Cast Lib is a clean, native .NET 8/C# framework for safely controlling
amateur-radio transceivers. It is not a line-by-line Hamlib port and must not
mechanically translate Hamlib source or data tables. Official manufacturer CAT
documentation is the authority. Hamlib is useful as a behavioural reference and
is an important compatibility target through optional adapters.

Primary objectives:

- Native asynchronous .NET APIs without native C interop.
- One managed runtime per physical radio with fully serialized CAT traffic.
- Multiple logical clients with identities, authorization, priorities, leases,
  exclusive operations, cleanup, and safe failure handling.
- Capability-driven clients that do not hard-code radio models.
- Modular drivers using stable model identifiers and common contracts.
- Native features that are not limited by the Hamlib network protocol.
- Optional REST, gRPC, richer TCP, desktop, web, service, and rigctld adapters
  around the same runtime rather than duplicated radio logic.
- Windows and Linux support.

The present scope is transceivers. The architecture may admit other equipment
categories later, but do not generalize prematurely.

## 2. Licensing and provenance

- License: `AGPL-3.0-only` for now. See `LICENSE`, `NOTICE`, and `AUTHORS`.
- Preserve maintainer/creator attribution in project documents.
- Requirements, architectural direction, hardware testing, and final decisions
  are human-directed. A substantial portion of code, tests, and documentation is
  AI-assisted. Maintain this transparency.
- Contributions must use compatible original work. Do not commit copyrighted
  manuals unless redistribution permission exists.

## 3. Workspace and repository facts

- Workspace: `C:\HAM_RADIO\PROJECTS\HAMLIB_PORT`
- Repository: `C:\HAM_RADIO\PROJECTS\HAMLIB_PORT\Rig2Cast`
- Solution: `Rig2Cast\Rig2Cast.sln`
- Target framework: .NET 8
- Shell: PowerShell
- Current baseline commit when this handover was written: `49eed20`
- Current automated suite: **163 passing tests**.

Git may report dubious ownership because Codex and the interactive Windows user
have different SIDs. Do not modify the user's global Git configuration. For
read-only Git commands use:

```powershell
git -c safe.directory=C:/HAM_RADIO/PROJECTS/HAMLIB_PORT/Rig2Cast -C Rig2Cast status --short
```

The worktree is intentionally dirty and contains the active milestone. Preserve
all existing changes. Do not reset, checkout, discard, or overwrite them. At the
time of writing, the principal uncommitted changes include:

- Receiver-targeted frequency/mode contracts and implementations.
- Receiver capability limits and stable JSON identities.
- Renewable and bounded transmit control.
- Firmware-aware Elecraft SWR capability handling.
- Console receiver syntax and safe PTT commands.
- Uniform runtime validation for legacy and receiver-targeted mutations.
- Receiver-specific typed observations and component freshness tracking.
- `TimeProvider`-based runtime timing and concurrent shutdown hardening.
- Plugin manifests, trusted discovery, isolated loading, diagnostics, and tests.
- New runtime, simulator, driver, and topology tests.
- Documentation and contribution-rule updates.

Always run `git status --short` before editing because the state may have changed
after this document was written.

## 4. Important project map

### Core libraries

- `src/Rig2Cast.Abstractions`: public radio, capability, driver, session,
  authorization, lease, control, meter, and event contracts.
- `src/Rig2Cast.Protocols`: reusable protocol engines. The shared ASCII CAT
  session owns framing, matching, cancellation boundaries, unsolicited routing,
  overflow reporting, and terminal fault behaviour.
- `src/Rig2Cast.Transports`: serial and future transport implementations.
- `src/Rig2Cast.Core`: driver catalog and core utilities.
- `src/Rig2Cast.Runtime`: managed radio, command scheduler, sessions, state
  reconciliation, leases, reconnect supervision, and renewable transmit control.
- `src/Rig2Cast.Simulator`: deterministic FTDX10-style simulator used for runtime
  and safety testing.

### Drivers

- `src/Rig2Cast.Drivers.Yaesu/Ftdx10`: Yaesu FTDX10 driver.
- `src/Rig2Cast.Drivers.Elecraft/K3Family`: shared K3S/K3/KX3/KX2 family driver
  with model profiles and option/firmware-dependent capabilities.

### Adapters and hosts

- `src/Rig2Cast.Adapters.Rigctld`: optional Hamlib-compatible TCP adapter.
- `samples/Rig2Cast.RigctldHost`: standalone rigctld host. Keep it a separate
  adapter/project; do not place radio-specific features in it.
- `samples/Rig2Cast.Console`: primary interactive hardware diagnostic surface.
- `samples/Rig2Cast.Ftdx10Smoke`: earlier FTDX10 hardware smoke tool.
- `src/Rig2Cast.PluginHost`: manifest validation, trusted assembly discovery,
  descriptor verification, duplicate isolation, and diagnostics. It is not yet
  wired into an application composition root.
- `src/Rig2Cast.Server`: scaffolding/future work.

### Tests

- `tests/Rig2Cast.Runtime.Tests`: current consolidated automated suite.
- `ReceiverTopologyTests.cs`: stable receiver identity, JSON round trip, and a
  synthetic three-receiver/no-VFO architecture proof.
- `ManagedRadioTests.cs`: serialization, sessions, leases, reconnect behaviour,
  PTT safety, renewable leases, delayed readback, and receiver runtime routing.
- `ElecraftK3DriverTests.cs`: Elecraft framing, mappings, observations, controls,
  firmware boundaries, and receiver operations.
- `Ftdx10DriverTests.cs`: FTDX10 command and capability fixtures.
- `YaesuAsciiProtocolTests.cs`: shared scripted transport and ASCII reliability
  tests despite the historical filename.
- `PluginHostTests.cs`: manifest, trust, metadata, compatibility, duplicate,
  failure-isolation, and assembly-loading fixtures.

## 4.1 Completion snapshot

Implemented and covered by automated tests:

- Shared ASCII CAT session reliability, including post-write cancellation and
  late-response correlation safety.
- Managed reconnect generations, stale queued-operation rejection, bounded event
  delivery, lease-expiry retry/diagnostics, reconnect de-keying, and cleanup that
  continues after failures.
- Concurrent `ManagedRadio.DisposeAsync` callers share the same completion and
  cleanup result. Observation streams that ignore cancellation cannot prevent the
  driver/transport from being disposed first.
- Runtime capability validation for receiver and legacy VFO frequency, active VFO,
  mode, passband, choices, and split mutations. Target-specific mode context is used
  when validating passband and choice values.
- Extensible receiver IDs, explicit receive/transmit signal paths, receiver-specific
  typed observations, component freshness, and rigctld ambiguity rejection.
- One injected `TimeProvider` for runtime delays, timers, lease expiry, reconnect and
  state timestamps, and event timestamps.
- Built-in Yaesu FTDX10 and Elecraft K3-family factories and drivers.
- Plugin-host library foundation: strict manifests, exact API compatibility,
  SHA-256 trust, safe entry paths, descriptor matching, duplicate handling,
  collectible load contexts, and per-manifest diagnostics.
- Diagnostic Console support for built-in model selection, receiver/VFO-targeted
  reads and writes, signal-path display, capability inspection, polling/watch,
  bounded PTT, and continuously renewed PTT.

Not implemented or not integrated yet:

- The Console and rigctld host do not reference `Rig2Cast.PluginHost`; they register
  built-in factories directly. There are no `--plugin-directory` or trust-store
  options and no user-facing plugin diagnostics yet.
- No persisted plugin trust-store/configuration format has been selected.
- `Rig2Cast.Server`, REST, gRPC, WebSocket, desktop, and web hosts remain future work.
- Icom CI-V and legacy Yaesu binary CAT protocol engines/drivers are not implemented.
- A general declarative command engine is not implemented. Existing family/model
  profiles are only partially declarative.
- The latest renewable-PTT and receiver-targeted physical checks listed in section 7
  still require explicit user confirmation.

## 5. Architectural invariants

Do not weaken these invariants to make a feature easier to implement.

### 5.1 Physical CAT ownership and concurrency

- One managed runtime/session owns a physical connection.
- All hardware commands pass through one `RadioCommandScheduler` and are
  serialized. Logical clients may be concurrent; serial bytes are not.
- A driver must not create a second unmanaged command path around the scheduler.
- Composite operations that must not interleave use the exclusive operation
  mechanism.

### 5.2 Capabilities are executable contracts

- A feature must not be advertised unless the selected model, installed options,
  firmware, target receiver/VFO, and driver implementation support it.
- The runtime validates capability support, ranges, choices, receiver targets,
  authorization, and leases before driver calls.
- Generic clients should derive controls from capabilities.
- Unsupported or ambiguous operations fail explicitly. Never silently choose a
  target to make a legacy call appear to work.

### 5.3 Receiver identity is not VFO identity

Read `docs/architecture/receiver-vfo-model.md` before receiver work.

- `ReceiverId` identifies a signal/receiver path (`main`, `sub`, `receiver-3`,
  slices, etc.). It is extensible and serializes as a stable JSON string/key.
- `VfoId` identifies a tuning register/source such as A, B, or Memory.
- Elecraft `$` is command-specific. It can mean VFO B, sub receiver, or a related
  secondary context. The driver interprets it; the common model must not.
- FTDX10 publishes one receiver (`main`) and VFO A/B registers.
- The user's K3S currently publishes only `main`; do not infer a physical sub
  receiver merely because `$` announcements exist.
- Legacy VFO-target APIs remain for compatibility during an additive migration.
- New controls, switches, choices, passband, meters, frequency, and mode have
  receiver-targeted paths.
- A synthetic three-receiver driver proves the receiver model does not require
  VFO A/B registrations.

### 5.4 ASCII protocol timeout semantics

- A response timeout makes an ASCII CAT session unsafe because a late response
  can poison a later query. `AsciiCatSession` therefore faults the session and the
  reconnect supervisor replaces it.
- Caller cancellation after a query frame has been committed creates the same
  ambiguity and also faults the session. Cancellation before commit remains safe.
- Do not change a timeout into a harmless empty value at the generic protocol
  layer.
- Prevent invalid/context-inapplicable queries through capabilities and runtime
  validation.
- Command writes have a cancellation boundary: cancellation must not truncate a
  frame already being written.
- Unsolicited frames and solicited replies share the stream and must be routed by
  validated, command-specific matching.

### 5.5 Transmit safety

- PTT requires an authorized client and a `radio.transmit` lease.
- Lease ownership is exclusive across clients.
- Lease expiry, owning-session disposal, shutdown, and renewal loss force RX.
- Lease-expiry de-key failures are diagnosed and retried with a bounded policy;
  they do not terminate the lease monitor.
- A reconnecting replacement that reports TX without a valid transmit lease is
  forced to RX before it can be published as connected.
- PTT-off uses safety priority.
- PTT mutations verify settled hardware state; `ManagedRadio` retries readback
  for up to one second to accommodate radio transition latency.
- `RenewingTransmitController` uses a short renewable lease for continuous PTT.
  The default lease is 10 seconds, renewed every 5 seconds. Loss of the owner or
  renewal leaves only the short safety window.
- Never enable hardware PTT in ordinary automated tests. Use the simulator.
- Tuner start remains intentionally unavailable from the Console.

### 5.6 Reconnect and shutdown

- Reconnect replaces the failed driver/transport generation; queued operations
  from an old generation must not run on a replacement radio.
- Serial transport disposal is used to unblock Windows reads that ignore
  cancellation.
- Exit/quit, Ctrl+C, session disposal, driver disposal, and observation-task
  shutdown must remain clean and bounded.
- Shutdown attempts de-keying but unconditionally continues through scheduler,
  driver, and transport cleanup, preserving or aggregating failures afterward.
- Do not terminate a user's running Console or rigctld process without explicit
  permission. A running process may lock output DLLs; build to a temporary output
  for validation or ask the user to exit it.

### 5.7 Plugin loading and lifetime

- Manifests are discovery metadata, not a trust mechanism. Production loading
  requires exactly one matching plugin ID and SHA-256 trust record.
- Development trust bypass must be explicit and must never become a production
  default.
- Reject unsafe paths, unknown manifest fields, incompatible API versions, duplicate
  plugin/model IDs, and any mismatch between sidecar and factory metadata.
- Isolate one plugin failure and report it without hiding or disabling valid plugins.
- Keep the `LoadedRadioPlugin` owner alive while its factory or drivers are in use.
  Collectible context unload is cooperative and mapped files may remain locked until
  all plugin references are unreachable.
- In-process plugins are trusted code with host permissions. Load contexts are not a
  hostile-code security boundary.

## 6. Supported hardware and local test environment

### Yaesu FTDX10

- Model ID: `yaesu.ftdx10`
- Local CAT port: `COM11`
- Tested baud: `38400` (user-configurable; FTDX10 maximum is 38400)
- Manual:
  `C:\HAM_RADIO\PROJECTS\HAMLIB_PORT\Rig2Cast\tcvr_manuals\FTDX-10_CAT_user.pdf`
- Topology: main receiver with VFO A/B registers.
- Automatic information is available and unsolicited FA/FB/IF and other frames
  are handled. Yaesu `FD` is a display/spectrum-related frequency announcement,
  not blindly another VFO identity; previous fixes addressed its handling.
- CAT PTT uses the driver's existing Yaesu TX commands and is lease protected.

### Elecraft K3S

- Model ID: `elecraft.k3s`
- Local CAT port: `COM14`
- Tested baud: `38400`
- Manual:
  `C:\HAM_RADIO\PROJECTS\HAMLIB_PORT\Rig2Cast\tcvr_manuals\K3S&K3&KX3&KX2 Pgmrs Ref, G5.pdf`
- Shared protocol family: K3S, K3, KX3, KX2 with profile-specific differences.
- Current radio main firmware response: `RVM05.62;`.
- Correct firmware query is `RVM;`, not `RV;`. Using `RV;` caused connection
  startup to retry/hang and was fixed.
- Elecraft `SW;` SWR reading was introduced in main firmware 5.66. Firmware 5.62
  returns no usable semicolon-terminated response. The driver retains `SW;` but
  advertises and permits SWR only at firmware 5.66 or later.
- SWR also has `RequiresTransmit = true`.
- Physical XMIT state is read correctly using `TQ;`; AI2 is not sufficient to
  guarantee immediate physical TX transition announcements. A forced state
  refresh queries `TQ;`.
- CAT PTT uses `TX;` and `RX;`. Real hardware PTT on/off, bounded lease expiry,
  status, and cleanup were reported working after settled readback was added.
- Current installed-option response/capabilities do not announce a physical sub
  receiver. `sub` operations should reject explicitly.

## 7. Hardware validation status

Physically validated during the development session:

- FTDX10 and K3S basic connection, state, frequency, mode, controls, unsolicited
  observations, reconnect, and clean exit work performed in earlier milestones.
- K3S `RVM;` returns and parses firmware 5.62.
- K3S firmware 5.62 no longer advertises or sends unsupported `SW;`.
- K3S `TQ;` distinguishes physical TX/RX after refresh.
- CAT PTT on/off and settled readback work. The user reported the requested PTT
  safety scenarios working on available hardware before the latest continuous
  renewal refactor.
- Explicit signal-path reporting was physically validated on 2026-09-02 on both
  radios. With split off, FTDX10 and K3S reported `main <- VFO A` for receive and
  transmit. With split on, both continued receiving on `main <- VFO A` and
  reported transmission on `main <- VFO B`; disabling split restored the active
  transmit path to A. The FTDX10 legacy `TransmitVfo` intentionally continued to
  report its configured split VFO B while split was off.

Implemented and automatically tested, but awaiting an explicit new hardware
confirmation after the latest changes:

- New semantics where bare `ptt on` renews until `ptt off`.
- Receiver-targeted `set frequency main ...` and `set mode main ...` for both
  physical drivers.
- Explicit rejection of `sub` frequency/mode on the user's no-sub K3S.

Do not claim these latest items physically validated until the user confirms.

## 8. Diagnostic Console grammar

Build:

```powershell
cd C:\HAM_RADIO\PROJECTS\HAMLIB_PORT\Rig2Cast
dotnet build .\samples\Rig2Cast.Console\Rig2Cast.Console.csproj --no-restore
```

K3S:

```powershell
.\samples\Rig2Cast.Console\bin\Debug\net8.0\Rig2Cast.Console.exe -- --model elecraft.k3s --port COM14 --baud 38400 --auto-information-mode 2 --allow-write
```

FTDX10:

```powershell
.\samples\Rig2Cast.Console\bin\Debug\net8.0\Rig2Cast.Console.exe -- --model yaesu.ftdx10 --port COM11 --baud 38400 --auto-information --allow-write
```

Important current commands:

```text
help
radio
state
refresh
capabilities [core|numeric|switches|choices|meters]
watch on
watch off
poll start 500
poll stop

get numeric AfGain [main|sub|A|B]
get switch ReceiveClarifier [main|sub]
get choice Preamp [main|sub|A|B]
meters [main|sub|A|B]
passband [main|sub|A|B]

set frequency <main|sub|A|B> <hz>
set mode [main|sub] <mode>
set passband [main|sub|A|B] <hz>
set numeric <name> [main|sub|A|B] <value>
set switch <name> [main|sub] <on|off>
set choice <name> [main|sub|A|B] <value>

ptt status
ptt on
ptt on <1..60 seconds>
ptt off
quit
```

Bare `ptt on` means continuously renewed PTT until `ptt off`. `ptt on 5` means
bounded PTT with automatic RX after lease expiry. Always use a dummy load and low
power for physical TX testing.

Historical grammar pitfall: there is no `control get ...` or singular `meter`.
Reads use `get numeric`, `get switch`, `get choice`, and plural `meters`.

## 9. Automated testing policy and commands

Every new feature or bug fix must include automated tests. This is an explicit
maintainer requirement, now also stated in `CONTRIBUTING.md`.

For driver features test, as applicable:

- Exact command encoding/framing.
- Valid response parsing and typed value mapping.
- Malformed, rejected, unrelated, and missing response behaviour.
- Unsolicited announcement parsing.
- Capability publication and target/range/choice accuracy.
- Model, installed-option, and firmware boundaries.
- Cancellation and disposal behaviour.

For runtime features test, as applicable:

- Authorization and lease requirements.
- Scheduler serialization and multi-client contention.
- Exclusive-operation non-interleaving.
- Cancellation boundaries.
- Disconnect/reconnect generation safety.
- Session and shutdown cleanup.
- State/readback latency and stale-observation behaviour.

For plugin features test, as applicable:

- Strict manifest parsing and path containment.
- API and static descriptor compatibility.
- Trust hash, missing trust, duplicate trust, and explicit development bypass.
- Duplicate plugin/model IDs and deterministic discovery order.
- Failure isolation, diagnostics, dependency resolution, lifetime, and disposal.

Standard test command:

```powershell
dotnet test .\tests\Rig2Cast.Runtime.Tests\Rig2Cast.Runtime.Tests.csproj --no-restore
```

Expected after plugin-host groundwork: 163 passed, 0 failed.

Console build:

```powershell
dotnet build .\samples\Rig2Cast.Console\Rig2Cast.Console.csproj --no-restore
```

Run `git diff --check` before handoff/commit:

```powershell
git -c safe.directory=C:/HAM_RADIO/PROJECTS/HAMLIB_PORT/Rig2Cast -C . diff --check
```

The complete solution build can fail only because a running Console or rigctld
host locks output assemblies. Do not misdiagnose a file lock as a compile error.
Do not kill the process automatically.

## 10. Current implementation details that are easy to get wrong

### Elecraft firmware discovery

- Startup order includes `OM;` and `RVM;` before enabling automatic information.
- Parser currently accepts `RVMNN.NN;`.
- Capabilities expose raw and normalized firmware metadata.
- Test `FirmwareBefore566DoesNotAdvertiseOrQuerySwr` guards the SWR boundary.
- `ScriptedRadioTransport` supplies a default modern `RVM05.66;` only when a
  test does not explicitly script `RVM;`; explicit firmware-boundary tests take
  precedence.

### Context-sensitive meters

- `RadioMeterDescriptor.RequiresTransmit` is additive availability metadata.
- Console bulk meter reads skip TX-only meters during RX.
- Firmware gating must prevent a context-invalid query entirely; receiving a
  newline is not a valid semicolon-terminated CAT reply.

### PTT readback latency

- The radio can physically transition before an immediate state query reflects
  it. `ManagedRadio.VerifyPttStateAsync` retries serialized state refreshes for up
  to one second.
- The simulator's `PttReadbackLagCount` exists to test this without hardware.
- A previous symptom was `ptt off` physically de-keying the K3S while the Console
  reported `on`; settled readback fixed it.

### Receiver frequency/mode semantics

- `SetFrequencyAsync(VfoId, ...)` addresses a VFO register.
- `SetFrequencyAsync(ReceiverId, ...)` tunes the receiver's selected signal path.
- FTDX10 `main` resolves the active VFO, then writes that register.
- Elecraft `main` reads IF to resolve the active path; `sub`, only when capability
  supported, maps to the command-specific secondary path/VFO B.
- Receiver-targeted mode follows the same distinction; Elecraft sub uses `MD$`.
- Runtime checks receiver capabilities and per-receiver limits before calling a
  receiver driver interface.
- Legacy VFO frequency, active-VFO, mode, and split mutations are also validated
  against writable capability support, advertised targets, ranges, and choices
  inside the serialized connection-generation boundary before driver invocation.
- Targeted choice and passband validation resolves the mode of the addressed
  receiver or persistent VFO instead of borrowing the selected receiver's mode.
- Managed runtime timers, delays, reconnect/state timestamps, event timestamps,
  and renewable transmit expiry decisions use an injected `TimeProvider` (or
  `TimeProvider.System` by default).

### JSON identity

- `ReceiverIdJsonConverter` writes receiver IDs as strings and supports dictionary
  property names. Do not replace this with object-shaped IDs in REST/gRPC JSON.

## 11. rigctld compatibility boundary

rigctld is intentionally a separate adapter/project. It supports a useful subset
of short/long commands, extended responses, multiple TCP clients, disconnect
cleanup, and native runtime serialization. It must remain compatibility-oriented:

- Do not add radio-specific functionality to rigctld.
- Do not constrain the native abstraction to what Hamlib can express.
- Map safely representable native concepts to Hamlib terminology.
- Return the appropriate failure/unsupported response when topology or features
  cannot be represented without ambiguity.
- PTT setters remain unavailable in the current rigctld adapter even if ordinary
  writes are enabled; changing this requires an explicit lease policy and tests.

Read `docs/rigctld-adapter.md` before adapter changes.

## 12. Next milestones in recommended order

### Milestone 1: checkpoint and preserve the current baseline

Before another structural milestone:

1. Run all 163 tests, build the Console, and run `git diff --check`.
2. Review the intentionally dirty worktree as one coherent change set. Do not
   discard or rewrite earlier user work and do not commit without explicit approval.
3. Ask the user for the outstanding physical confirmation:
   - On each radio, run bare `ptt on`, wait 15-20 seconds, confirm it remains on,
     then run `ptt off` and confirm RX.
   - Test receiver-targeted main frequency/mode on FTDX10 and K3S.
   - Confirm unsupported K3S sub frequency/mode rejects without disconnecting.

Automated evidence is already sufficient to continue non-hardware work; physical
validation is a release-confidence checkpoint, not permission to weaken safety.

### Milestone 2: integrate plugins into a composition host

The plugin library foundation is complete; user-facing discovery is not. Implement
integration additively without moving discovery into drivers or runtime:

1. Define a host-owned JSON configuration containing one or more plugin directories,
   trust records (`pluginId` plus SHA-256), and an explicit development-mode flag.
   Reject duplicate trust identities. Do not silently enable development mode.
2. Add `Rig2Cast.PluginHost` to the chosen composition root, initially the diagnostic
   Console. Discover before model selection, register each successful factory in the
   existing `RadioDriverCatalog`, and keep `LoadedRadioPlugin` instances alive for as
   long as any catalog registration or driver instance can reference them.
3. Add CLI options such as `--plugin-directory <path>`, `--plugin-trust <path>`, and
   an unmistakably unsafe development option. Preserve built-in factories and
   existing command grammar.
4. Print concise load diagnostics and include plugin models in `--list-models`.
   A bad plugin must not prevent built-in radios or other valid plugins from loading.
5. Add integration tests for mixed built-in/plugin catalogs, duplicate IDs across
   those sources, missing directories, malformed trust stores, deterministic load
   order, lifetime/disposal, and operation of a fixture plugin through the catalog.
6. Only after the Console path is stable, reuse the same composition helper from the
   rigctld host or future server rather than duplicating loading policy.

Trust is the security boundary. `AssemblyLoadContext` provides dependency/lifetime
isolation, not process isolation or a sandbox. If untrusted third-party drivers must
be supported later, use a separate process and IPC; do not imply that in-process
loading is safe for hostile code. See `docs/plugin-host.md` and ADR 0004.

### Milestone 3: stabilize the external driver SDK contract

Before inviting independent driver distribution:

1. Review the public abstractions and manifest schema for versioning guarantees.
   Keep the initial compatibility rule exact until a documented minor-version policy
   and compatibility tests exist.
2. Provide a minimal external sample plugin and sidecar manifest that references only
   permitted lower-level projects. Include hash-generation and local trust setup
   instructions, but do not ship private manuals or copied vendor material.
3. Add automated architectural dependency checks so driver assemblies cannot
   reference adapters, hosts, UI, or server projects.
4. Define plugin replacement/update behavior. Do not unload a context while its
   factory, descriptor, driver, tasks, or event handlers remain reachable.
5. Decide packaging and signing only after the contract is stable. SHA-256 trust pins
   exact bytes; it does not establish publisher identity or provide automatic update
   trust.

Instance `RadioCapabilities` remain authoritative after opening a radio. The manifest
and factory descriptor provide static selection/connection metadata and must not
pretend to know firmware- or option-dependent capabilities.

### Milestone 4: declarative engine foundation

A declarative layer is useful for regular model families and especially legacy Yaesu
binary status blocks, but it must not become a universal protocol interpreter. Build
it below drivers and beside protocol-specific framing engines:

1. Inventory repeated declarations already present in FTDX10 and Elecraft profiles:
   command identifiers, access, targets, mode applicability, value ranges, choice
   mappings, encode/decode rules, firmware/option gates, and observation mappings.
2. Define immutable, strongly typed C# descriptors first. Validate them at factory
   construction with duplicate-command, invalid range/step, missing mapping,
   impossible target, and ambiguous-response checks. Avoid external JSON/YAML until
   descriptor semantics and versioning are proven.
3. Keep framing, stream correlation, addressing, echo handling, rejection semantics,
   pacing, and session-terminal rules inside the appropriate protocol engine. A
   declaration may describe a payload; it must not erase protocol state machines.
4. Provide explicit code escape hatches for conditional queries, composite operations,
   unusual replies, installed-option discovery, firmware quirks, and safety-sensitive
   behavior. PTT/leases stay in the managed runtime, never in data files.
5. Pilot the descriptors on a small read-only slice with golden encoding/decoding and
   malformed-input tests. Measure whether a new model can mostly supply data while
   still allowing ordinary driver code.
6. If external declarative files are later desired, add schema versions, strict
   validation, bounded sizes, source provenance, and optionally a source generator.
   Do not execute expressions or scripts from a declarative profile.

Success means less repetitive model code and easier audited additions, not zero C#.
CI-V should inform the abstraction but should not be forced through an ASCII-oriented
declaration model.

### Milestone 5: Icom CI-V protocol family

CI-V is the recommended binary architectural stress test. Implement a separate engine
beside `AsciiCatSession`; do not add binary switches to the ASCII engine:

1. Choose one documented Icom model/CI-V address and record exact official source
   revision and assumptions. The user does not own Icom hardware, so mark all results
   simulated/unvalidated.
2. Build a byte-oriented incremental frame decoder with preamble resynchronization,
   controller/radio addressing, partial and concatenated frames, echo discrimination,
   ACK/NAK/error handling, maximum-frame bounds, and cancellation/timeout semantics.
3. Define query correlation for addressed binary replies and route transceive frames
   as typed unsolicited observations. Prove late replies cannot satisfy later queries.
4. Create deterministic scripted fixtures and a CI-V simulator before a model driver.
   Cover noise, repeated preambles, wrong addresses, echo on/off, malformed frames,
   timeout, cancellation after commit, disconnect, and clean shutdown.
5. Implement the first driver as a narrow read-only slice: identify, frequency, mode,
   state, and capabilities. Add setters only after readback and runtime validation are
   proven. Add PTT last and retain the normal lease/safety path.
6. Seek community hardware validation before claiming production support.

### Milestone 6: legacy Yaesu binary CAT

Implement this as another binary family engine, not as an FTDX10 ASCII extension:

1. Select one officially documented model and capture fixed command lengths, pacing,
   status-block layouts, BCD/endian rules, busy/rejection behavior, and model quirks.
2. Keep generic fixed-frame I/O, pacing, timeout/cancellation, and resynchronization in
   the protocol engine. Put command layouts, status offsets, scale factors, and model
   capabilities in immutable profiles—the strongest initial declarative-engine use.
3. Add golden byte-vector tests, truncated/shifted status-block tests, invalid BCD,
   unsupported command, timing, cancellation, and disconnect cases.
4. Start read-only, then introduce setters with verified readback. Add transmit only
   after the same lease and forced-RX invariants are demonstrated.

### Later roadmap

- Reorganize tests into protocol-focused projects when CI-V or another binary family
  makes the consolidated runtime test project unwieldy.
- Add fuzz/property-style framing tests for binary decoders and declarative profiles.
- Implement the standalone server and native REST/gRPC/WebSocket surfaces around the
  existing runtime; keep adapters thin and capability-driven.
- Consider deprecating legacy `VfoId.Main`/`Sub` aliases only in a planned major API.
- Extend receiver-specific typed observations only when real protocol semantics need
  additional independently fresh state components.

## 13. Relevant architecture and decision documents

Read these before related work:

- `README.md`
- `CONTRIBUTING.md`
- `docs/architecture.md`
- `docs/architecture/receiver-vfo-model.md`
- `docs/concurrency-and-leases.md`
- `docs/plugin-host.md`
- `docs/decisions/0004-trusted-plugins.md`
- `docs/decisions/0005-transmit-leases.md`
- `docs/diagnostic-console.md`
- `docs/rigctld-adapter.md`
- `docs/protocol-sources/yaesu-ftdx10.md`
- `docs/protocol-sources/elecraft-k3-family.md`
- `docs/ftdx10-coverage.md`
- `docs/code_review_01.md`, `docs/code_review_02.md`, `docs/code_review_03.md`, and
  `docs/findings.md` for
  historical review context; do not blindly implement rejected findings.

## 14. Working rules for the next AI agent

1. Start by reading this document and the directly relevant architecture file.
2. Inspect `git status`, active processes, and the current test count.
3. Preserve user changes and avoid destructive Git/filesystem operations.
4. State assumptions and distinguish automated, simulated, and physical evidence.
5. Do not claim hardware behaviour based only on a scripted fixture.
6. Add tests with every feature or fix and update the README test count.
7. Prefer additive API migration until the maintainer explicitly approves a
   breaking major version.
8. Keep drivers modular and protocol-specific; keep runtime safety generic.
9. Keep adapters thin and capability-driven.
10. For PTT or other hazardous operations, preserve leases, time bounds, verified
    readback, safety priority, cleanup, and opt-in physical tests.
11. When a CAT query times out, assume stream correlation may be unsafe and let
    reconnect recovery replace the session.
12. Ask for physical testing only after automated fixtures pass, and provide exact
    commands plus expected outcomes.

## 15. Suggested first commands in a resumed session

From `C:\HAM_RADIO\PROJECTS\HAMLIB_PORT`:

```powershell
git -c safe.directory=C:/HAM_RADIO/PROJECTS/HAMLIB_PORT/Rig2Cast -C Rig2Cast status --short
dotnet test .\Rig2Cast\tests\Rig2Cast.Runtime.Tests\Rig2Cast.Runtime.Tests.csproj --no-restore
dotnet build .\Rig2Cast\samples\Rig2Cast.Console\Rig2Cast.Console.csproj --no-restore
```

Then compare the result with the expected 163 tests and inspect changes made after
this handover. Continue with the physical-validation checkpoint or plugin composition
integration in section 12; receiver/signal-path stabilization and plugin-host library
groundwork are already complete. Do not restart the architecture from scratch.
