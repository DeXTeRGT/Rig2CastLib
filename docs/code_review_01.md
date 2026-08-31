Here's an AI analysis of your project, and Ill attach an AI prompt that will build it all:  I pulled the repo down and read the whole thing. Here's the analysis and the agent prompt.
The prompt is above. Summary of what I found in the code, since this drove the structure:
The core can't support other vendors as written. The biggest blocker is YaesuAsciiProtocol.QueryAsync, which reads one byte at a time straight off the transport and rejects anything not starting with the expected two-letter prefix. That works only because the FTDX10 never speaks unsolicited. It fails outright on Icom CI-V (the bus echoes your own frame back before the reply), Kenwood/Elecraft AI modes, and legacy Yaesu binary CAT (no terminator, length depends on the opcode you sent). Phase 0 of the prompt is a Rig2Cast.Protocol read pump with pluggable framers.
Other real defects I flagged: RadioState has IsSplit but no transmit VFO, so Kenwood FR/FT and dual-RX split are inexpressible; Revision is hard-coded to 1 in both the driver and the simulator; Ftdx10Driver.OpenAsync disposes a transport it doesn't own; frequency limits are literals in the driver instead of coming from the capability ranges that already exist; RadioDriverCatalog is an unsynchronized Dictionary; RadioEventHub uses unbounded per-subscriber channels; serial defaults are 2 stop bits with RTS handshake, wrong for CI-V and dangerous where DTR keys PTT. There's also no state cache, so several rigctld clients polling S-meter will saturate a 9600-baud CI-V link.
The two highest-leverage additions in the prompt are the cross-driver conformance suite (one abstract test class every driver must pass, deriving its assertions from the driver's own declared capabilities) and the declarative model profile, which makes a sibling radio a data change rather than a code change.
On ANAN specifically: there's no ANAN CAT protocol as such. Thetis and PowerSDR mRX expose a TS-2000-compatible subset plus a large ZZxx extended set over serial or TCP, same shape as SmartSDR CAT. I grouped those as one family and required implementing TcpRadioTransport, which is in the enum today with no implementation.
File would not load  it is a markup file.  I can send in email.  Here it is in text: # Agent Prompt: Multi-Vendor CAT Driver Expansion for Rig2CastLib

Copy everything below the line into your coding agent (Claude Code, Codex, Copilot Workspace, etc.) as the task brief. It is written to be self-contained.

---

# ROLE

You are a senior .NET engineer with deep amateur-radio CAT protocol experience. You are extending github.com/DeXTeRGT/Rig2CastLib, an AGPL-3.0 .NET 8 transceiver-control framework, from a single supported radio (Yaesu FTDX10) to broad multi-vendor coverage.

You will do three things, in this order:

1. *Fix the core.* The current abstractions cannot express Icom CI-V, legacy Yaesu binary CAT, or any radio that pushes unsolicited state. Fix that first. Do not write new drivers on top of a broken foundation.
2. *Add drivers*, organized by protocol family, driven by declarative model profiles.
3. *Prove it works* with a layered test strategy: unit, golden fixture, cross-driver conformance suite, end-to-end integration, and opt-in hardware validation.

Work in reviewable increments. Never open a pull request larger than roughly 1500 changed lines.

---

# NON-NEGOTIABLE CONSTRAINTS

## Licensing and protocol provenance

The repository has an explicit policy in docs/protocol-provenance.md. Follow it exactly.

- *Never copy, translate, or mechanically transliterate Hamlib source.* Not its functions, driver tables, command tables, comments, calibration curves, magic constants, or tests. Hamlib is GPL/LGPL and this project is AGPL-3.0 with a stated independent-implementation posture. Treating Hamlib as a spec sheet is a licensing and integrity failure.
- Hamlib may be consulted *only* to discover which features exist and which quirks are worth verifying. Anything you learn that way must then be independently sourced from a manufacturer document before it enters the code.
- The *authority* for every implemented command is the manufacturer's published CAT / CI-V / PC-control reference manual, or behavior observed on physical hardware by the project owner.
- For every model you add, create docs/protocol-sources/<vendor>-<model>.md following the existing yaesu-ftdx10.md template: document title, revision code, SHA-256 of the PDF, and a per-feature validation status table (documented / simulator-verified / hardware-verified).
- If a manual and observed hardware disagree, implement the hardware behavior and record the deviation explicitly, as the FTDX10 PR speech-processor entry already does.
- Do not commit third-party copyrighted PDFs to the repository. Reference them by title, revision, and hash.

## Transmit safety

RF safety outranks feature completeness.

- No new code path may key a transmitter without a valid LeaseKinds.Transmit lease.
- Serial control-line assertion is a real PTT hazard. On many interfaces DTR keys PTT and RTS keys CW. SerialRadioTransportOptions must gain explicit DtrEnable and RtsEnable properties defaulting to false, and the transport must set them deliberately before opening rather than inheriting SerialPort defaults. Document this in the transport XML docs.
- Antenna tuner start, transverter enable, and full break-in changes are exclusive operations, not ordinary setters.
- Any command that can cause carrier emission must be listed in the model profile as EmissionCapable = true, and the runtime must refuse it without a transmit lease even when a driver implements it.

## Quality gates

Directory.Build.props sets TreatWarningsAsErrors and AnalysisLevel=latest-recommended. The build must stay clean. A PR that suppresses an analyzer rather than fixing the cause will be rejected.

---

# PART 1: REPOSITORY ORIENTATION (verified, current as of this brief)

src/
  Rig2Cast.Abstractions/      contracts only: capabilities, controls, drivers, transports, sessions
  Rig2Cast.Core/              RadioDriverCatalog
  Rig2Cast.Runtime/           ManagedRadio, RadioCommandScheduler, RadioLeaseManager, RadioEventHub
  Rig2Cast.Drivers.Yaesu/     YaesuAsciiProtocol + Ftdx10Driver (765 lines) + Ftdx10CatProfile
  Rig2Cast.Transports/        SerialRadioTransport
  Rig2Cast.Adapters.Rigctld/  rigctld-compatible TCP server
  Rig2Cast.Simulator/         SimulatedFtdx10Driver, InMemoryRadioTransport
  Rig2Cast.PluginHost/        PluginManifest (scaffold only)
tests/Rig2Cast.Runtime.Tests/ 6 test files, ~49 tests
samples/                      Demo, Console, Ftdx10Smoke, RigctldHost

Key contracts you must understand before changing anything:

- IRadioTransport: ConnectAsync / WriteAsync(ReadOnlyMemory<byte>) / ReadAsync(Memory<byte>). A raw byte pipe with no framing and no ownership of a read loop.
- IRadioDriver plus optional IRadioControlDriver, IRadioSwitchDriver, IRadioChoiceDriver, IRadioMeterDriver. Drivers implement whichever they support and ManagedRadio type-tests at call time.
- RadioCapabilities: manufacturer, model, VFO set, frequency ranges, mode set, and four dictionaries of RadioControlId / RadioSwitchId / RadioChoiceId / RadioMeterId descriptors, plus an untyped Extensions bag.
- RadioState: revision, connection status, IReadOnlyDictionary<VfoId,long> frequencies, active VFO, mode, IsSplit, IsTransmitting.
- RadioCommandScheduler: two unbounded channels (safety, normal), one processor loop, strict serialization of all CAT traffic.

The existing FTDX10 driver is a good model of care: bounds-checked parsers, explicit response-shape validation, YaesuProtocolException on malformed frames, mode-aware choices. Preserve that rigor. Do not lower it to make a table-driven approach easier.

---

# PART 2: CORE DEFECTS AND REQUIRED CHANGES (Phase 0)

These are the specific problems found in the current code. Each is a work item. Fix them before adding vendors. Each gets its own PR with tests.

## C1. There is no read pump, so unsolicited data is unrepresentable

YaesuAsciiProtocol.QueryAsync writes a command then reads the transport *one byte at a time* until a semicolon, and throws if the resulting frame does not start with the expected prefix. This works only because the FTDX10 never speaks unless spoken to.

It breaks completely for:

- Kenwood and Elecraft AI1 / AI2 auto-info, where the radio pushes FA, MD, IF and others whenever a front-panel control moves.
- Icom CI-V transceive mode, where any bus device broadcasts to address 0x00.
- Icom CI-V echo. The CI-V bus is a single wire; your own transmitted frame comes back at you before the reply does. The current design would treat the echo as the response.
- Elecraft K4 and Icom network radios where other clients cause state change.

*Required design:*

Introduce Rig2Cast.Protocol with a single owning read pump per transport:

public interface IFrameReader
{
    // Attempts to extract exactly one complete frame from the head of the buffer.
    // Returns false if more bytes are needed. Advances 'consumed' past discarded garbage.
    bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> frame);
}

public interface IFrameDispatcher
{
    // Returns true if this frame satisfies the pending request; false routes it to unsolicited.
    bool Matches(ReadOnlySpan<byte> frame, PendingRequest request);
}

Build the pump on System.IO.Pipelines. One PipeReader fed by a background task doing buffered transport.ReadAsync into a PipeWriter. Byte-at-a-time serial reads are both slow and fragile; stop doing them.

The pump routes each complete frame to one of:

1. the single in-flight PendingRequest if the dispatcher matches it,
2. an echo-suppression filter (CI-V),
3. an unsolicited channel that feeds RadioEventHub as RadioEventKind.StateChanged,
4. a diagnostic channel for unparseable bytes (bounded, rate-limited, never throws).

Provide three concrete framers, all covered by unit tests including split-across-read boundaries and garbage-prefix cases:

- TerminatedFrameReader(byte terminator) for Yaesu ASCII, Kenwood, Elecraft (;).
- DelimitedFrameReader(preamble, terminator) for CI-V (FE FE ... FD), which must resynchronize on truncated frames and tolerate the collision-detect case where a partial frame appears.
- FixedLengthFrameReader(Func<opcode,int> lengthResolver) for legacy Yaesu binary, where responses have no terminator and length is determined by the command that was sent.

## C2. YaesuAsciiProtocol generalizes Yaesu-specific behavior

Two hard-coded assumptions will corrupt other vendors:

- ValidatePrefix demands *exactly two ASCII letters*. Kenwood/Elecraft SDR emulations use four-character ZZxx commands; Yaesu menu reads such as EX030201 have longer echoes; CI-V has no letter prefix at all.
- Frame() calls ToUpperInvariant() on every command. Elecraft uses a case-and-symbol-significant $ suffix for sub-receiver variants (FA$, FW$). Uppercasing is safe for Yaesu only.

Move YaesuAsciiProtocol into Rig2Cast.Drivers.Yaesu as a family-specific codec built on the generic pump. Do not promote it to a shared base class.

## C3. RadioState cannot express split correctly

RadioState has IsSplit but no transmit VFO. That is insufficient for:

- Kenwood FR`/FT`, which is literally a receive-VFO / transmit-VFO selector pair.
- Yaesu FTDX101 and Kenwood TS-990 dual-receiver split.
- rigctld get_split_vfo / set_split_vfo, which take a VFO argument.
- Cross-band split on IC-9700 and satellite operation generally.

Add VfoId TransmitVfo and long? SplitTransmitFrequencyHz to RadioState, and add SetSplitAsync(bool enabled, VfoId transmitVfo, ...) to the driver contract with the existing two-arg form kept as a default-to-B convenience. Update the rigctld adapter accordingly.

## C4. RadioState.Revision is hard-coded to 1

Both Ftdx10Driver.ReadStateAsync and SimulatedFtdx10Driver.ReadStateAsync construct new RadioState(1, ...). Revisions are supposed to be monotonic and are used by clients to detect staleness. Drivers should not assign revisions at all. Move revision ownership to ManagedRadio, which stamps a monotonically increasing value on every state it publishes. Same for RadioCapabilities.Revision and RadioAvailability.Revision.

## C5. Driver disposes a transport it does not own

Ftdx10Driver.OpenAsync calls await transport.DisposeAsync() in its catch block, but the transport was constructed and passed in by the caller (Ftdx10DriverFactory / the host). Ownership is ambiguous and the host will double-dispose. Decide explicitly: the *factory* owns transport lifetime. Drivers never dispose a transport they were handed. Add an ownsTransport flag if a driver genuinely needs to, and default it to false.

## C6. RadioMode and VfoId are too small for the target set

RadioMode is missing at minimum: Dv (D-STAR), C4fm, Dd, WideFm, PacketFm, Sam, Fsk / FskReverse as distinct from Rtty, and CwNarrow. More importantly, **an unmapped native mode currently collapses to Unknown and the original code is lost**, so a read-modify-write cycle silently changes the radio's mode.

Required: a RadioModeValue record carrying both the canonical RadioMode and the raw native token, so unknown modes round-trip losslessly. Drivers expose IReadOnlyDictionary maps in their model profile and never throw on an unrecognized read.

VfoId conflates two orthogonal concepts. Main`/Sub` are receivers; A`/B` are VFOs. Radios that have both (FTDX101, TS-990S, IC-9700, K3 with sub-RX, IC-7610) cannot be described. Introduce a ReceiverId (Main, Sub, Third...) and let VFO selection be scoped per receiver. Flex-style slice radios need indexed receivers; design for at least 8.

## C7. Hard-coded frequency limits inside the driver

Ftdx10Driver.SetFrequencyAsync throws below 30 kHz and above 75 MHz using literals. This belongs in FrequencyCapability.Ranges, which already exists and is already populated. Validate against capabilities, not literals. IC-9700 reaches 1.3 GHz and IC-905 reaches 10 GHz; the literal approach does not scale. Also enforce *transmit* ranges separately from receive ranges, which the FrequencyRange record already models but nothing checks.

## C8. No connection supervision or reconnect

ConnectionStatus.Faulted exists and is never assigned. There is no reconnect, no re-identification after reconnect, and no capability refresh. RadioEventKind.CapabilitiesChanged and ConnectionChanged are declared and never published.

Add a RadioConnectionSupervisor in the runtime: detect transport fault or repeated timeout, transition to Faulted, publish ConnectionChanged, then optionally reconnect with exponential backoff and jitter, re-run identification, re-probe capabilities, and publish CapabilitiesChanged if the capability revision differs. Make backoff policy injectable and drive it from TimeProvider so tests are deterministic.

## C9. No state cache, so multi-client reads hammer the CAT port

Every ReadControlAsync goes to the wire. With several rigctld clients polling S-meter and frequency, a 9600-baud CI-V link will saturate and user commands will queue behind polls. This is the single most likely real-world failure of the multi-client promise.

Add a coalescing state cache in ManagedRadio:

- Per-item freshness policy (frequency 100 ms, S-meter 200 ms, static menu items indefinite until invalidated).
- Request coalescing so N concurrent identical reads produce one CAT transaction.
- Invalidation on any write to a related item and on any unsolicited push.
- Push-preferred mode: when the radio supports auto-info or transceive, enable it and treat the cache as authoritative, falling back to polling when the radio does not.

## C10. Scheduler issues

RadioCommandScheduler.ProcessAsync calls WaitToReadAsync on both channels then Task.WhenAny, allocating two tasks per idle iteration and abandoning the loser's continuation each time. Under a long idle it accumulates. Replace with a single wait on a shared signal, or use one channel of (priority, item) with a small priority heap.

Two more scheduler gaps:

- *No per-command timeout.* A driver that hangs blocks the entire radio forever. Add a scheduler-level deadline with a configurable default and a per-command override.
- *Cancellation mid-write corrupts the radio's parser.* If a caller cancels after a partial CAT frame is on the wire, the radio is left mid-frame and the next command is misparsed. This is severe on CI-V. Rule: once the first byte is written, the command is non-cancellable; cancellation may only remove queued work. Enforce this in the scheduler and test it.

## C11. RadioDriverCatalog is not thread-safe and has no plugin loading

Plain Dictionary with no synchronization, mutated by Register while Models and TryFind read. Use ConcurrentDictionary or lock. Separately, Rig2Cast.PluginHost/PluginManifest.cs is a scaffold with no loader, despite docs/driver-development.md describing manifest discovery plus a SHA-256 trust record. Either implement it in this workstream or explicitly defer it and file an issue. Do not leave dead scaffolding.

## C12. RadioEventHub uses unbounded per-subscriber channels

A slow or abandoned rigctld client grows memory without limit. Switch to bounded channels with BoundedChannelFullMode.DropOldest and publish a diagnostic event carrying the dropped count so operators can see it.

## C13. Serial defaults are Yaesu-shaped and wrong for other vendors

SerialRadioTransportOptions defaults to StopBits.Two and Handshake.RequestToSend. Icom CI-V is typically 8N1 with no handshake. Kenwood varies by model and baud (the TS-890S supports two stop bits only at 4800). Elecraft is typically 8N1 none.

Move framing parameters into RadioModelDescriptor as a SerialProfile record (data bits, stop bits, parity, handshake, DTR, RTS, inter-command delay, default and supported baud rates) and have the host build transport options from the selected model rather than from a global default. Add InterCommandDelay, which several older radios genuinely need.

## C14. Meter calibration honesty

RadioMeterDescriptor carries CalibrationAvailable, which is currently false everywhere. rigctld's STRENGTH level is defined as dB relative to S9. Do not fabricate a conversion. When calibration is unavailable, the rigctld adapter must report the documented "unknown" path rather than emitting a fake dB value. Add per-model calibration tables only when backed by measured hardware data, and record the measurement method in the provenance doc.

## C15. rigctld adapter completeness

Real clients (N1MM+, WSJT-X, Log4OM, GridTracker, CQRLOG) probe more than the current command set. Add, with tests:

- dump_state and \dump_caps, populated from RadioCapabilities rather than hard-coded.
- \chk_vfo, get_powerstat, \get_vfo_info.
- l / L levels and u / U functions, backed by an explicit, tested mapping table from Hamlib level and function tokens to RadioControlId and RadioSwitchId.
- Correct negative rigctl error codes (-1 RIG_EINVAL, -11 RIG_ENAVAIL, and so on) instead of generic failures.
- Both short and long forms and the extended-response separator format for every added command.

---

# PART 3: DRIVER ARCHITECTURE

Restructure so that a new sibling model is a data change, not a code change.

Rig2Cast.Protocol/                shared pump, framers, BCD/ASCII codecs, echo suppression
Rig2Cast.Drivers.Yaesu/
  Cat/                            semicolon ASCII family
  Legacy5Byte/                    binary FT-817/857/897/847 family
  Models/                         yaesu.ftdx10.json, yaesu.ftdx101d.json, ...
Rig2Cast.Drivers.Icom/
  CiV/                            frame codec, addressing, echo, transceive
  Models/
Rig2Cast.Drivers.Kenwood/
Rig2Cast.Drivers.Elecraft/
Rig2Cast.Drivers.SdrCat/          TS-2000-emulating SDRs (Thetis/ANAN, SmartSDR CAT, ExpertSDR2)
Rig2Cast.Drivers.Misc/            Ten-Tec, Alinco, Xiegu, QRP Labs
Rig2Cast.Testing/                 shared test harness, shipped as its own project

## Model profile format

Each model is a strongly typed profile object, source-generated from or deserialized with System.Text.Json source generation (no reflection-based serialization; keep trimming and AOT viable). A profile declares:

- identity: model id (icom.ic7300), manufacturer, display model, driver id
- transport profile: supported transports, serial parameters, baud rates, default, inter-command delay
- identification: expected ID response, and for CI-V the default address plus the settable range
- frequency ranges, per band, receive and transmit separately
- mode map: canonical mode to native token, bidirectional, with unmapped tokens preserved
- VFO and receiver topology
- command table: for each control / switch / choice / meter, the get command, set template, response prefix, field offsets, width, scale, offset, units, min, max, step, applicable modes, required lease
- quirks: echo behavior, response delays, known manual deviations, firmware-gated features

Exceptional behavior stays in C#. The profile handles the regular 90 percent; a driver may override any member for the irregular 10 percent. The FTDX10's IS and CF001 signed parsers are exactly the kind of thing that stays as code.

*Migrate the FTDX10 to this shape first*, and require that its existing tests pass unchanged. That is your proof the abstraction is adequate before you scale it.

## Capability probing

Static profiles cannot know about the optional 300 Hz roofing filter, whether a K3 sub-receiver is installed, whether an ATU is present, or which firmware is loaded. After identification, run an optional probe pass that adjusts the capability set, then publish CapabilitiesChanged. Probes must be read-only and must degrade gracefully to "assume absent" on timeout.

---

# PART 4: TARGET RADIOS BY PROTOCOL FAMILY

Implement family by family. Within a family, do one reference model to hardware-verifiable depth, then add siblings as profiles.

## Family A: Yaesu ASCII CAT (semicolon-terminated)

Command and response are printable ASCII terminated by ;. Reads are the two-letter command plus ;; sets carry parameters. No unsolicited traffic on most models.

Reference model: *FTDX10* (already implemented, migrate to profiles).

Targets: FTDX101D and FTDX101MP (dual receiver, Main/Sub), FTDX5000, FTDX3000, FTDX1200, FT-710, FT-991A, FT-891, FT-950, FT-2000, FT-450D, FTDX9000.

Watch for: parameter widths differ between generations for the same mnemonic; FT-2000 and FT-950 differ from the DX-series; the MD VFO selector digit is not universal; FTDX101 adds Main/Sub variants of many commands; EX menu commands have model-specific menu numbering that must live in the profile, never in shared code.

Sources: Yaesu publishes a separate "CAT Operation Reference Manual" PDF per model at yaesu.com under Files/Downloads. Record FileID, filename, revision code, and hash.

## Family B: Yaesu legacy binary CAT (5-byte)

Fixed 5-byte commands: four parameter bytes followed by the opcode byte. Frequency is packed BCD. Responses have *no terminator* and a fixed length that depends on the command sent, so the framer must be length-driven from the outstanding request. Some status reads return a single packed byte of bit flags.

Reference model: *FT-857D* or *FT-817ND*.

Targets: FT-817 / FT-818, FT-857 / FT-857D, FT-897 / FT-897D, FT-847, FT-100D.

Watch for: the command set is genuinely limited, so many capabilities must be declared Unsupported rather than faked; the FT-847 early firmware lacks read capability entirely; CTCSS byte layout differs between FT-817 and FT-857/897. *Do not implement undocumented EEPROM-write commands.* Yaesu has attributed flash failures to third-party software doing exactly that, and Ham Radio Deluxe removed those code paths for that reason. Restrict this family to documented opcodes and say so in the provenance doc.

This family is your best stress test that the framing abstraction is real and not a Yaesu-ASCII wrapper.

## Family C: Icom CI-V

Binary frames: FE FE <to-addr> <from-addr> <cmd> [<sub-cmd>] [<data...>] FD. Frequency is BCD, little-endian, typically 5 bytes for modern radios and 4 for older ones. Positive acknowledgement is FB, negative is FA. Controller address is conventionally 0xE0. Address 0x00 is broadcast and is used by transceive mode.

Reference model: *IC-7300* (address 0x94).

Targets and default addresses (verify each against the model's CI-V reference guide, do not trust any third-party table including this one): IC-7300 0x94, IC-9700 0xA2, IC-705 0xA4, IC-7610 0x98, IC-7851/IC-7850 0x8E, IC-7800 0x6A, IC-9100 0x7C, IC-7100, IC-7610, IC-7700, IC-7600, IC-7410, IC-746Pro, IC-756ProIII, IC-718, IC-905, plus the IC-R8600 receiver.

Mandatory CI-V-specific handling:

- *Echo suppression.* The CI-V bus is single-wire and echoes your transmission. Match and discard your own frame before matching the reply. On USB CI-V ports echo is a configurable radio setting, so support both: detect at connect time by sending a benign read and observing whether the echo arrives.
- *Address filtering.* Discard frames not addressed to your controller address or to 0x00.
- *Collision detection.* If the echo does not match what was sent, another controller is on the bus. Back off and retry with jitter; surface a diagnostic event.
- *Transceive mode.* When enabled, the radio broadcasts frequency and mode changes to 0x00. Route these to the unsolicited channel and feed the state cache. Make enabling it a per-model policy, since it increases bus load.
- **FA (NG) responses** must map to a distinct exception type, not a generic parse failure. NG usually means "valid frame, unsupported or out-of-range parameter," which is actionable capability information.
- Address must be user-overridable in RadioConnectionOptions, because operators change it.

Optional follow-on, not in initial scope: the LAN/WLAN control protocol on IC-705/IC-9700/IC-7610/IC-905 (UDP 50001-50003) is a completely different protocol from CI-V. File it as a future transport, do not stub it.

## Family D: Kenwood ASCII

Semicolon-terminated ASCII, superficially like Yaesu but semantically different. FA`/FB` are VFO A/B frequency; FR`/FT` select receive and transmit VFO independently, which is the correct model for split; IF; returns a single fixed-width status string containing frequency, RIT/XIT, mode, VFO, TX state and more; AI1`/AI2` enable auto-info push.

Reference model: *TS-590SG*.

Targets: TS-590S and TS-590SG, TS-890S, TS-990S (dual receiver), TS-480SAT/HX, TS-2000, TS-870S, TS-570D/S.

Watch for: **the IF response length differs between models.** Parse by documented field offsets from that model's profile, never by a shared constant. TS-990S main/sub requires the receiver abstraction from C6. Auto-info must be enabled deliberately and disabled on disconnect, otherwise you leave the radio in a state that confuses the next application.

Sources: Kenwood publishes "PC CONTROL COMMAND Reference Guide" PDFs at kenwood.com/i/products/info/amateur/pdf/, for example ts590_g_pc_command_en_rev3.pdf.

## Family E: Elecraft

Kenwood-derived but a strict superset with its own conventions.

Reference model: *K3S* (the project owner has stated K3/K3S is the intended next hardware target, so build this to hardware-verified depth).

Targets: K3, K3S, K4/K4D/K4HD, KX2, KX3, K2.

Elecraft-specific requirements:

- *Meta modes.* K20`-K23` and K30`-K31` change the set/response format of other commands. The driver must set a known meta mode at connect, record it, and restore the prior mode on disconnect. Getting this wrong silently changes the meaning of FW, MD and IF.
- **$ sub-receiver suffix.** FA$, FW$, MD$ and similar address the sub receiver. This is exactly why C2 (do not uppercase, do not assume two-letter prefixes) matters.
- *Auto-info.* AI0 polling, AI1 IF-on-change, AI2 full front-panel event push. Prefer AI2 with the state cache, and always restore AI0 on disconnect.
- *K4 multi-client.* The K4 natively supports multiple simultaneous clients over USB, RS-232 and Ethernet, and maintains its own server-side state. Rig2Cast should be a well-behaved client of that, not fight it. Document the interaction with Rig2Cast's own lease model.
- **EX menu commands** are firmware-version-sensitive. Gate them on the version read at connect.

Sources: ftp.elecraft.com hosts the K3S/K3/KX3/KX2 Programmer's Reference and the K4 Programmer's Reference, both revision-stamped.

## Family F: SDR transceivers presenting a Kenwood TS-2000 CAT surface

This is the ANAN answer and it matters for the request as stated. Apache Labs ANAN radios are controlled through Thetis or PowerSDR mRX on a host PC. That software exposes a CAT interface over serial (usually a virtual COM pair) or TCP that implements a **TS-2000-compatible command subset plus a large ZZxx extended set**. FlexRadio's SmartSDR CAT does the same thing: ZZxx native commands plus a TS-2000 subset for legacy clients.

Reference model: *ANAN via Thetis* (anan.thetis model id).

Targets: ANAN-10E/100/200D/7000DLE/8000DLE/G2 via Thetis or PowerSDR mRX; Hermes-Lite 2 via Thetis or piHPSDR; FlexRadio 6000/8000 series via SmartSDR CAT; SunSDR2/MB1 via ExpertSDR2 TS-2000 emulation.

Design guidance:

- Model these as a KenwoodTs2000Compatible base profile with a per-host *extension profile* for ZZ commands. Thetis and SmartSDR overlap heavily but are not identical, and Thetis has Andromeda-specific commands.
- Prefer ZZ forms where a functional equivalent exists, since the TS-2000 subset is lossy for SDR features. FA and ZZFA are interchangeable on these hosts.
- *Support TCP as a transport for this family*, not just serial. RadioTransportKind.Tcp already exists in the enum with no implementation. Implement TcpRadioTransport here.
- Capability probing matters more than usual: what a Thetis build supports is determined by its CATStructs.xml, which changes between releases. Probe, do not assume.
- Do not attempt the native SmartSDR TCP/IP API (port 4992) or the TCI WebSocket protocol in this workstream. Both are richer and deserve their own driver later. Note them in the roadmap.

## Family G: long tail

Add as profiles once the families above are stable and only where a manufacturer document exists:

- Ten-Tec Omni VII, Eagle, Orion II, Jupiter (mixed ASCII and binary, model-specific).
- Xiegu G90, X6100, X6200 (CI-V-like; verify the address and any deviations, do not assume Icom compatibility).
- Lab599 Discovery TX-500.
- QRP Labs QMX/QDX and (tr)uSDX, both of which emulate a Kenwood TS-480 subset.
- Alinco DX-SR8, DX-SR9.
- Yaesu FT-1000MP, FT-920, FT-990, FT-1000D (older Yaesu binary, distinct from the 5-byte family).

For every one of these, if you cannot find a manufacturer document, *do not implement it*. Log it in docs/roadmap.md as blocked on documentation. Guessing from third-party code is a provenance violation.

---

# PART 5: TESTING STRATEGY

The existing suite is 49 tests. Expect several hundred. Testing is not a phase at the end; each PR lands with its tests.

## Layer 1: unit tests, pure functions

Target the codecs directly, with no transport involved.

- BCD encode/decode round-trip, little-endian and big-endian, 4-byte and 5-byte, including boundary and invalid-nibble cases.
- ASCII field encode/decode with scale, offset, sign, and zero-padding, including the signed forms (Yaesu IS, CF001, Kenwood RIT).
- Mode map bidirectionality: every canonical mode encodes, every native token decodes, unmapped tokens survive a round trip.
- Frame construction for every command in every model profile: assert exact bytes.
- Framer tests: complete frame, frame split across three reads, two frames in one read, garbage prefix, truncated frame followed by a valid one, oversized frame rejection.

Use *property-based testing* (FsCheck) for the codecs. decode(encode(x)) == x over the declared range, and encode never produces a frame outside the declared width, are the two properties that catch the most real bugs. This is significantly more valuable here than more example tests.

## Layer 2: golden fixtures

Create tests/fixtures/<vendor>/<model>/*.json. Each fixture is a list of request/response byte pairs with provenance:

{
  "model": "icom.ic7300",
  "source": { "document": "IC-7300 CI-V Reference Guide", "revision": "...", "sha256": "...", "page": 12 },
  "verification": "documented",
  "exchanges": [
    { "name": "read frequency 14.250 MHz",
      "send": "FEFE94E003FD",
      "receive": "FEFEE0940300502514 00FD",
      "expect": { "frequencyHz": 14250000 } }
  ]
}

verification is one of documented, simulator-verified, hardware-captured. A fixture marked hardware-captured must record the radio model, firmware version, and capture date, mirroring the existing FTDX10 provenance doc. Add a fixture-capture mode to the smoke-test sample so the project owner can generate these from real hardware in one run.

A single fixture-driven test class replays every fixture for every model. Adding a model adds coverage with no new test code.

## Layer 3: cross-driver conformance suite

This is the highest-value item in the whole plan. Write one abstract xUnit base class that every driver must pass:

public abstract class RadioDriverConformanceTests
{
    protected abstract ValueTask<IRadioDriver> CreateAsync(CancellationToken ct);
    protected abstract RadioModelDescriptor Descriptor { get; }

    // ~40 shared tests, all derived from the driver's own declared capabilities
}

Required conformance assertions:

- *Capability self-consistency.* Every declared control, switch, choice and meter is readable. Every declared writable one accepts its minimum, its maximum and a mid-range value, and rejects min-1, max+1 and an off-step value with ArgumentOutOfRangeException. Every choice option encodes and decodes. Every declared frequency range accepts its endpoints; one Hz outside is rejected.
- *Unsupported means unsupported.* Any feature not in the capability set throws NotSupportedException and never silently succeeds. This is the invariant capability-driven UIs depend on.
- *Round-trip state.* Set frequency, mode, VFO and split, then read back and assert equality. Run it for every declared VFO and every declared mode.
- *Frequency edges.* Band edges, the 0-Hz and negative rejection, and the widest supported frequency for the model.
- *Transport failure.* Disconnect mid-command produces a specific exception, not a hang. Malformed response produces the driver's protocol exception. Timeout produces TimeoutException within the configured deadline plus tolerance, measured against a fake TimeProvider, not a wall clock.
- *Cancellation.* A token cancelled before dispatch cancels; a token cancelled after the first byte is written does not corrupt the stream (see C10).
- *Unsolicited interleaving.* Inject an unsolicited frame in the middle of a pending request and assert the request still resolves correctly and the unsolicited frame reaches the event hub.
- *Echo interleaving* (CI-V drivers): inject an echo and assert it is discarded.
- *Idempotence.* Two identical reads with no intervening write return equal values.
- *No emission.* Assert that no test in the suite ever produces a transmit-capable command. Enforce it by having the simulator throw if it sees one outside an explicitly transmit-flagged test.

## Layer 4: integration tests, full stack

Against per-model simulators, through ManagedRadio and through the rigctld adapter.

- Multi-client: N concurrent sessions issuing interleaved reads and writes; assert CAT commands are strictly serialized (the simulator already tracks MaximumConcurrentOperations, use it, assert it stays at 1).
- Lease enforcement: PTT without lease is refused; with a lease it succeeds; on lease expiry, transmit is dropped.
- *Dead-man safety* (new): client disconnects while transmitting, and the runtime returns the radio to receive within a bounded time. Also a maximum-transmit-duration guard. Add this; it does not exist today and it is essential for remote operation.
- Exclusive scope: assert no other client's command interleaves inside ExecuteExclusiveAsync.
- Scheduler priority: a safety-priority command overtakes a queued backlog.
- State cache: N concurrent identical reads produce exactly one CAT transaction; a write invalidates the relevant cache entries; an unsolicited push updates the cache without a poll.
- Reconnect: fault the transport, assert Faulted is published, assert reconnect, re-identify, and capability republication.
- rigctld: a scripted client session per supported command, including dump_state, error codes, both command forms, and multiple simultaneous clients.

*Determinism rules.* No Task.Delay and no wall-clock assertions anywhere in the test suite. Inject TimeProvider everywhere (ManagedRadio.CreateAsync already accepts one; propagate it into the scheduler, the supervisor, the cache, and the leases). Simulated latency and jitter are `TimeProvider`-driven. Tests that depend on real time are the reason CI suites rot.

## Layer 5: fault injection

Extend the simulator harness with an injectable fault policy: response delay, dropped response, truncated response, corrupted byte, extra unsolicited frames, spurious echo, wrong-address CI-V frames, and mid-frame disconnect. Every driver runs the conformance suite once clean and once under each fault mode, asserting the driver either recovers or fails with a specific typed exception. Never a hang, never a silent wrong value.

## Layer 6: hardware validation, opt-in

Tag with [Trait("Category", "Hardware")], excluded from default dotnet test, run via --filter Category=Hardware with the port supplied by environment variable.

- *Read-only by default.* Writes require a second explicit opt-in flag.
- Never key the transmitter. No test in this category may call PTT or tuner start.
- Produce a machine-readable validation report per run (model, firmware, date, per-command pass/fail) that can be pasted into the model's provenance document, matching the format already used in docs/protocol-sources/yaesu-ftdx10.md.

## CI

GitHub Actions, matrix over windows-latest and ubuntu-latest:

- dotnet format --verify-no-changes
- dotnet build -warnaserror
- dotnet test with coverlet; fail under 80 percent line coverage on Rig2Cast.Abstractions, Rig2Cast.Runtime, Rig2Cast.Protocol, and every driver project
- a public-API snapshot test so accidental breaking changes to Rig2Cast.Abstractions are visible in the diff
- fixture provenance validation: every fixture references a documented source, and every model with a driver has a provenance doc

---

# PART 6: C# STANDARDS

The existing code is already good on most of these. Hold the line.

- .NET 8, C# 12, nullable enabled, implicit usings, warnings as errors, file-scoped namespaces.
- sealed by default. record for immutable value contracts; readonly record struct for small hot-path values such as frame headers and CI-V addresses.
- ValueTask for the async surface, CancellationToken on every async method and actually honored, ConfigureAwait(false) throughout library code, no async void.
- Guard clauses via ArgumentNullException.ThrowIfNull, ArgumentException.ThrowIfNullOrWhiteSpace, ArgumentOutOfRangeException.ThrowIf*.
- Zero allocation in the framing hot path: ReadOnlySpan<byte>, ReadOnlySequence<byte>, ArrayPool<byte>, System.IO.Pipelines. No string concatenation and no LINQ inside the read pump or the codec. LINQ is fine in capability construction and other cold paths.
- No regular expressions in protocol parsing. Span-based parsing only. If a regex is unavoidable elsewhere, use [GeneratedRegex].
- System.Text.Json with a source-generated JsonSerializerContext for profiles and fixtures. Keep the library trim-friendly and AOT-viable.
- Microsoft.Extensions.Logging.Abstractions with source-generated LoggerMessage partial methods. Add structured logging for every CAT transaction at Trace with a redaction hook, Debug for state transitions, Warning for retries and faults. There is currently no logging at all, which will make field diagnosis of a remote station miserable. **Never log at a level that produces per-byte output at Information or above.**
- TimeProvider for all time. No DateTimeOffset.UtcNow in testable paths; the drivers currently call it directly in every read and should take it from the injected provider.
- Exceptions: one typed exception hierarchy rooted at RadioProtocolException, with per-family subclasses (CiVNegativeAcknowledgementException, YaesuProtocolException, and so on). Never throw or catch bare Exception. Never swallow.
- XML doc comments on every public type and member. Enable GenerateDocumentationFile.
- InternalsVisibleTo for test projects only, declared in Directory.Build.props.
- Prefer composition to inheritance for drivers. The only base class should be the conformance test base; drivers compose a codec, a framer, and a profile.
- One type per file, matching filename. Keep any single driver file under about 400 lines; the current 765-line Ftdx10Driver.cs should shrink substantially once profiles carry the tables.

---

# PART 7: DELIVERY PLAN

Sequence of pull requests. Each is independently reviewable, green, and documented.

| PR | Scope |
|----|-------|
| 1 | C4, C5, C11, C12, C13 (small correctness and safety fixes) with tests |
| 2 | Rig2Cast.Protocol: pump, three framers, echo suppression, dispatcher, full unit suite |
| 3 | Rig2Cast.Testing: scripted transport, fixture replay, fault injection, conformance base class |
| 4 | C3, C6, C7 (state, mode, VFO/receiver, frequency-range model) and migrate FTDX10 to profiles, existing tests unchanged |
| 5 | C8, C9, C10 (supervisor, state cache, scheduler) with deterministic TimeProvider tests |
| 6 | C15 rigctld completeness plus adapter tests |
| 7 | Family A: FTDX101D reference plus FT-991A, FT-891, FT-710 profiles |
| 8 | Family C: CI-V codec plus IC-7300 reference, full echo/collision/transceive coverage |
| 9 | Family C siblings: IC-9700, IC-705, IC-7610, IC-7100, IC-7851, IC-7800 |
| 10 | Family D: Kenwood, TS-590SG reference plus TS-890S, TS-2000, TS-480 |
| 11 | Family E: Elecraft, K3S reference plus K4, KX3, KX2 |
| 12 | TcpRadioTransport plus Family F: ANAN/Thetis reference plus SmartSDR CAT, Hermes-Lite 2 |
| 13 | Family B: legacy Yaesu binary, FT-857D reference plus FT-817/818, FT-897, FT-847 |
| 14 | Family G long tail, documentation, model matrix, roadmap |

---

# PART 8: DEFINITION OF DONE, PER MODEL

A model is done when all of the following hold:

1. docs/protocol-sources/<vendor>-<model>.md exists with document title, revision, SHA-256, and a per-feature validation table.
2. A model profile exists and the model is registered in RadioDriverCatalog.
3. The full conformance suite passes against the model's simulator, clean and under every fault-injection mode.
4. Golden fixtures exist for every implemented command, each with a provenance reference.
5. docs/<model>-coverage.md exists in the format of the current ftdx10-coverage.md, listing implemented, unsupported, and not-yet-implemented features honestly. Declaring Unsupported is a valid and preferred outcome; silently pretending is not.
6. The rigctld adapter exposes everything the model supports that the protocol can express, with tests.
7. No new build warnings. Coverage thresholds hold.
8. README.md supported-radio matrix updated with the model's verification level: documented, simulator-verified, or hardware-verified.

*Do not mark anything hardware-verified that you have not run against hardware.* The project owner has physical FTDX10 and plans K3/K3S. Everything else is at most simulator-verified until someone with the radio confirms it, and the README must say so. Overstating verification is the fastest way to destroy trust in a framework like this.

---

# PART 9: PRIMARY SOURCE STARTING POINTS

Fetch, hash, and record each of these before implementing the corresponding family. This list is a starting point, not a substitute for finding the exact revision that matches the target firmware.

- Yaesu: per-model "CAT Operation Reference Manual" PDFs at yaesu.com Files/Downloads (FT-710, FTDX101MP/D, FT-991A, FT-891, and so on). Legacy 5-byte CAT is documented inside the FT-817ND / FT-857D / FT-897D operating manuals.
- Icom: per-model "CI-V Reference Guide" PDFs from icomjapan.com / icomeurope.com. Some older models document CI-V only inside the full instruction manual.
- Kenwood: "PC CONTROL COMMAND Reference Guide" PDFs at kenwood.com/i/products/info/amateur/pdf/.
- Elecraft: K3S/K3/KX3/KX2 Programmer's Reference and K4 Programmer's Reference at ftp.elecraft.com.
- FlexRadio: SmartSDR CAT User's Guide (documents the ZZ set and the TS-2000 compatible subset).
- Thetis / PowerSDR: the PowerSDR CAT Command Reference Guide plus the CATStructs.xml shipped with the installed application, which is the authoritative list of what a given build actually supports.

Ambiguity resolution order: manufacturer document, then observed hardware behavior, then ask the project owner. Never third-party source code.

---

# WHAT TO DO FIRST

Before writing any code, produce and post for review:

1. A gap analysis confirming or correcting each defect C1 through C15 against the current tree.
2. The proposed Rig2Cast.Protocol public API surface.
3. The model profile schema.
4. The conformance test list, as test-method names.

Get agreement on those four, then start at PR 1. Do not begin driver work until PR 4 has landed and the FTDX10 passes its existing tests through the new abstraction unchanged.