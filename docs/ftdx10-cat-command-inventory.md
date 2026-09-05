# Yaesu FTDX10 CAT command inventory

Initial audit date: 2026-09-05. The inventory was subsequently updated after the
approved operator-control batch was implemented.

## Authority and method

- Source: `tcvr_manuals/FT-DX10_CAT_Operating Manual.pdf`
- Title/revision: *FTDX10 CAT Operation Reference Manual*, `2103-C` (2021)
- SHA-256: `52CF66CAEBC3F06B1AF1EAD7AF821F7DE63604F63148B6437CDA4EDC8A33D5F7`
- Python/PyPDF extraction covered all 25 pages, including the command-list and
  Set/Read/Answer/AI tables.
- Compared with `Ftdx10Driver`, its factory/profile, automated tests, and the
  existing coverage record.

The implementation was originally verified against newer Yaesu revision `2308-F`.
That manual adds the physically verified, write-only `FS` command, absent from the
`2103-C` command list. Revision differences and recorded hardware deviations must
not be merged blindly. In particular, hardware proved that `PR` uses 0/1 for
off/on despite this manual documenting 1/2; see the [protocol source
record](protocol-sources/yaesu-ftdx10.md).

## Result

The `2103-C` table contains **101 command families**:

- **43 implemented** for the represented function/subcommands;
- **7 partially implemented**;
- **51 not implemented**;
- plus later-manual `FS`, implemented write-only.

The driver has strong live-radio coverage, but not yet most of the complete CAT
collection. Missing areas are memory/QMB, messages and playback, repeater/CTCSS,
front-panel actions, persistent menu settings, alarms/status, scan, power, and
scope configuration.

## Implemented families (43)

| CAT | Function | Rig2Cast representation |
| --- | --- | --- |
| `AG` | AF gain | `AfGain` numeric |
| `AO` | AMC output level | Mode-aware `AmcOutputLevel` numeric |
| `AI` | Automatic information | Connection lifecycle and observations |
| `AV` | Anti-VOX level | `AntiVoxLevel` numeric |
| `BC` | Auto notch | `AutoNotch` switch |
| `BI` | Break-in | `BreakIn` switch, mode-aware |
| `BP` | Manual notch state/frequency | Switch plus `ManualNotchFrequencyHz` |
| `CO` | Contour/APF state/frequency | Two switches and two numeric controls |
| `FA`, `FB` | VFO A/B frequency | VFO frequency read/write/observations |
| `GT` | AGC | `Agc` choice |
| `ID` | Identification | Open-time model verification |
| `IS` | IF shift | `IfShiftHz` numeric |
| `KP`, `KS` | CW pitch/speed | `CwPitchHz`, `KeyerSpeedWpm` |
| `KR` | Electronic keyer | Mode-aware switch |
| `LK` | Dial lock | `DialLock` switch |
| `MD` | Mode | Common mode read/write; ambiguous AI reports request full refresh |
| `MG` | Microphone gain | Mode-aware numeric |
| `ML` | Monitor state/level | Switch and numeric |
| `NA` | Narrow filter | Switch |
| `NB`, `NL` | Noise blanker/state level | Switch and numeric |
| `NR`, `RL` | Noise reduction/state level | Switch and numeric |
| `PA` | Preamp/IPO | Choice |
| `PC` | RF power | `TransmitPower` numeric |
| `PL` | Processor level | Numeric |
| `PR` | Speech processor and parametric microphone EQ | Two distinct switches using hardware-proven 0/1 encoding |
| `RA` | Attenuator | Choice |
| `RF` | Roofing filter | Choice with asymmetric read/write codes |
| `RG` | RF gain | Numeric |
| `RT`, `XT` | RX/TX clarifier enables | Two switches |
| `SH` | IF width | Mode-aware passband/filter-width choice |
| `SM` | S-meter | Raw meter |
| `SQ` | Squelch | Numeric |
| `SD` | Semi break-in delay | Mode-aware discrete millisecond choice |
| `TX` | CAT TX | Lease-protected common PTT |
| `VD` | VOX/data-VOX delay | Discrete choice |
| `VG` | VOX gain | Numeric |
| `VS` | VFO selection | Active VFO A/B |
| `VX` | VOX | Switch |

`FS0`/`FS1` from revision `2308-F` is exposed as a write-only, mode-aware tuning
step. The driver correctly does not send the unsupported `FS;` query.

## Partial families (7)

| CAT | Present | Missing / decision |
| --- | --- | --- |
| `AC` | Tuner enable/bypass | Tuning start/stop must remain a separate hazardous action |
| `CF` | Main-band clarifier frequency | Combined flags, sub-band form, and other qualified fields |
| `EX` | APF width (`EX030201`) | Almost the entire Table 2 menu hierarchy |
| `IF` | Authoritative VFO-A frequency/mode | Memory, clarifier, CTCSS, repeater, and context fields are not surfaced |
| `OI` | Authoritative VFO-B frequency/mode | Same additional fields as `IF` are not surfaced |
| `RM` | Selectors 3–8: COMP/ALC/PO/SWR/IDD/VDD | Selectors 0/1; `SM` already supplies S-meter |
| `ST` | Split off/on | Quick-split `2` (+5 kHz) is a separate action |

## Missing but compatible with current control concepts (15)

These need shared IDs/descriptors and sometimes targeting, not a new subsystem.

| CAT | Function | Candidate representation |
| --- | --- | --- |
| `BY` | Busy/squelch-open | Read-only receiver status/switch |
| `CN` | CTCSS frequency | Targeted choice using the documented table |
| `CS` | CW spot | CW-only switch or momentary action after hardware semantics are checked |
| `CT` | CTCSS mode | Targeted off/encode/decode choice |
| `DA` | Display/LED levels | Three configuration numerics |
| `DT` | Date/UTC time | Typed clock configuration |
| `FN` | Fine tuning | Mode-aware switch |
| `MS` | Front-panel meter selection | Choice; low priority beside independent `RM` reads |
| `OS` | Repeater shift | FM-only targeted choice |
| `RI` | SWR/record/play/TX-inhibit info | Typed alarms/status and observations |
| `RS` | Normal/menu status | Read-only operating-context state |
| `SC` | Scan | Action/choice plus status and ownership semantics |
| `SF` | Knob assignment | Persistent configuration choice |
| `SS` | Scope settings | Controls/choices/switches; this is **not spectrum sample data** |
| `UL` | Unlock | Clarify semantics/golden response before mapping to lock/status |

## Missing momentary/action commands (19)

A generic capability-described `RadioActionId`/driver/session API is preferable to
misrepresenting these as persistent switches. It should describe parameters,
destructiveness, required role/lease, expected readback, and cache invalidation.

| CAT | Function | Note |
| --- | --- | --- |
| `AB`, `BA` | Copy A→B / B→A | Destructive to destination VFO |
| `BD`, `BU` | Band down/up | Target-qualified momentary actions |
| `BS` | Band select | Target plus typed band argument |
| `CH` | Memory channel up/down | Navigation action |
| `DN`, `UP` | Microphone down/up | UI emulation; low native priority |
| `ED`, `EU` | Encoder down/up | Parameterized UI emulation |
| `QI`, `QR` | QMB store/recall | Mutating state actions |
| `QS` | Quick split | Compound state-changing action |
| `RC` | Clarifier clear | Destructive/reset action |
| `RD`, `RU` | Clarifier down/up | Relative adjustment; absolute `CF` is safer |
| `SV` | Swap VFOs | Mutates both VFO assignments |
| `VM` | Main/VFO to memory | Resolve overlap with memory workflow first |
| `ZI` | CW zero-in | CW-only momentary action |

## Missing memory/message/playback subsystem (14)

| CAT | Function | Shared model needed |
| --- | --- | --- |
| `AM`, `BM` | Store VFO A/B to memory | Destination/overwrite-safe memory action |
| `MA`, `MB` | Memory to VFO A/B | Recall/copy action |
| `MC` | Select memory channel | Typed memory identity/selection |
| `MR` | Read memory | `RadioMemory`: frequency, mode, clarifier, CTCSS, repeater, tag |
| `MT`, `MW` | Read/write/tag memory | Memory CRUD and overwrite policy |
| `KM` | Keyer memory text | Typed CW message memory |
| `KY` | CW memory playback | TX-capable mode-aware action |
| `EM` | RTTY/DATA message memory | Typed digital message memory |
| `EN` | RTTY/DATA playback | TX-capable action |
| `LM` | DVS recording | Audio-message recorder action/state |
| `PB` | DVS playback | Playback action; establish TX behavior |

These contracts should be cross-vendor. Transmitting playback requires the
existing transmit lease or an equally strict specialized lease.

## Missing hazardous/lifecycle commands (3)

| CAT | Function | Treatment |
| --- | --- | --- |
| `MX` | MOX | Overlaps PTT; prefer lease-protected `TX` absent a proven distinction |
| `PS` | Radio power | Dedicated lifecycle capability, timed double-send, transport-specific behavior, reconnect consequences |
| `TS` | TXW | Establish RF semantics; require authorization and bounded transmit lease |

## Recommended roadmap

1. Correct documentation terminology so partial families are not called complete.
2. Add the broadly reusable live controls: `AO`, `BY`, `CN`, `CT`, `CS`, `FN`,
   `KR`, `OS`, and `SD`, with target/mode metadata and hardware tests.
3. Design a shared parameterized **action abstraction**, then add safe copy/swap,
   quick-split, zero-in, scan, band, and relative-control operations.
4. Add shared **alarm/status** contracts and semantic observations.
5. Design a whitelist-based persistent **radio settings** API. Never expose
   arbitrary `EX` menu paths: calibration/reset, firmware, CAT baud/RTS, and other
   connection-destructive settings need separate policy or must remain excluded.
6. Design typed **memory** and **message/playback** APIs. Start read-only; add
   destructive writes and transmitting playback last.
7. Add `SS` scope configuration independently from future framed-input fan-out and
   typed spectrum streaming.
8. Keep power, tuner-start, MOX/TXW, resets, calibration, firmware update, and
   transmit-capable playback outside ordinary controls.

## Cross-vendor conclusion

The existing abstractions already cover steady-state VFO, frequency, mode, split,
PTT, passband, numeric, switch, choice, meter, target, applicability, and semantic
observation behavior. The reusable gaps exposed by this manual are:

1. Parameterized/momentary actions with destructive and cache-invalidating metadata.
2. Typed memory channels and safe CRUD/copy workflows.
3. Message/keyer/DVS storage and playback with transmit safety.
4. Persistent radio settings distinct from live controls.
5. Typed alarms/status indicators and observations.
6. Radio power/lifecycle operations.
7. Later, typed spectrum streaming (`SS` itself only configures the scope).

Icom CI-V and other manufacturers expose the same concepts using different wire
protocols. These should be common capability-described contracts, with ASCII,
CI-V, and future binary drivers supplying protocol-specific implementations.
