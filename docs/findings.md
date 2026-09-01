# Code Review Findings

---

## BUG-01 — Non-atomic disposal in `Ftdx10Driver`

**File:** `src/Rig2Cast.Drivers.Yaesu/Ftdx10/Ftdx10Driver.cs:103, 509`

`_disposed` is `bool`. Concurrent `DisposeAsync` calls race — both can pass the guard and double-dispose the transport and protocol.

```csharp
// wrong
private bool _disposed;
if (!_disposed) { _disposed = true; ... }

// fix
private int _disposed;
if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
```

Also update `EnsureActive` at line 929:
`ObjectDisposedException.ThrowIf(_disposed, this)` →
`ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this)`

---

## BUG-02 — FTDX10 `IF` observation discards VFO and split state

**File:** `src/Rig2Cast.Drivers.Yaesu/Ftdx10/Ftdx10Driver.cs:607`

The `IF` frame parser extracts only frequency and mode. Active VFO (`frame[2]`) and split (`frame[24]`) are ignored. `ApplyObservation` falls back to stale cached values via `?? _state.X`, silently lying to clients in AI mode.

**FTDX10 IF frame layout (28 chars, confirmed):**

| Position | Field |
|---|---|
| 0–1 | `"IF"` |
| 2 | VFO mode (`'0'`=A, `'1'`=B, `'2'`=Memory) |
| 3–4 | Memory channel |
| 5–13 | Frequency (9 digits) ← confirmed |
| 14 | Clarifier sign |
| 15–18 | Clarifier offset |
| 19 | RIT |
| 20 | XIT |
| 21 | Mode ← confirmed |
| 22 | FM bandwidth |
| 23 | SCAN |
| 24 | SPLIT (`'0'`=off, `'1'`/`'2'`=on) |
| 25 | CTCSS tone |
| 26 | CTCSS code |
| 27 | `';'` |

> ⚠ Positions 2 and 24 deduced from protocol family knowledge — cross-check against FTDX10 CAT Operation Reference §IF before merging.

TX status is **not** in the IF frame; `IsTransmitting = null` in the observation is correct.

```csharp
// fix: extend validation and populate missing fields
VfoId activeVfo = frame[2] == '1' ? VfoId.B : VfoId.A;
bool isSplit = frame[24] != '0';
return new(StateInformation, observedAt, frame,
    activeVfo, frequency, mode,
    IsSplit: isSplit,
    TransmitVfo: isSplit ? OppositeVfo(activeVfo) : activeVfo,
    ActiveVfo: activeVfo);
```

---

## BUG-03 — Elecraft K3 AGC missing third code (`GT000`)

**File:** `src/Rig2Cast.Drivers.Elecraft/K3Family/ElecraftK3Driver.cs:317, 344, 605`

K3 has three valid AGC codes. Only two are handled. `GT000;` throws `ElecraftProtocolException` in `ReadChoiceAsync` and in `ParseObservation` (silently swallowed → `Unknown`). Affects four sites:

| Site | Fix |
|---|---|
| `ReadChoiceAsync` | Add `"GT000;" => "off"` |
| `WriteChoiceAsync` | Add `"off" => "GT000"` |
| `ParseObservation` GT branch | Add `"GT000;" => "off"`; change unhandled wildcard from `throw` to `return Unknown` |
| `CreateChoiceCapabilities` | Add `["off"] = new("off", "Off")` |

> ⚠ Verify fast/slow label order (`GT002`/`GT004`) against K3 Programmer's Reference before finalising — current mapping may be swapped.

---

## BUG-04 — FTDX10 RM meter extraction reads wrong digit count

**File:** `src/Rig2Cast.Drivers.Yaesu/Ftdx10/Ftdx10Driver.cs:373, 92`

Confirmed format: `RM3000000;` — 10 chars, **6-digit** value at positions 3–8. `AsSpan(3, 3)` reads only the first 3 digits. `raw > 255` cap and `raw / 255d` normalisation are also wrong for a 6-digit field.

`MeterCommand` record needs `Digits` and `MaximumRaw` fields:

```csharp
// TODO: confirm RmMaximumRaw from FTDX10 CAT Operation Reference §RM
//       or by reading RM5 at full TX power. Placeholder matches SM0 ADC scale.
private const int RmMaximumRaw = 255;

[SignalStrength] = new("Signal strength", "SM0", "SM0", 7,  3, 255),
[Compression]   = new("Compression",     "RM3", "RM3", 10, 6, RmMaximumRaw),
// ... all RM entries: Digits=6, MaximumRaw=RmMaximumRaw
```

Extraction and validation become:
```csharp
!int.TryParse(response.AsSpan(command.ResponsePrefix.Length, command.Digits), ..., out int raw) ||
raw < 0 || raw > command.MaximumRaw)
...
return new RadioMeterReading(meter, raw, raw / (double)command.MaximumRaw, ...);
```

---

## DESIGN-01 — Protocol classes are ~85% duplicated

**Files:** `YaesuAsciiProtocol.cs`, `ElecraftAsciiProtocol.cs`

`ReadLoopAsync`, `FailPending`, `FailSession`, `DisposeAsync`, `_transactionGate`, `_pendingGate` — identical in both. Differences are: prefix length rule, space-in-frame allowance, `?;` rejection handling. A shared `AsciiRadioProtocol` base class or a `FramingConfig` struct would halve the maintenance surface before adding more protocol families.

---

## DESIGN-02 — `YaesuAsciiProtocol` prefix validation locked to 2 chars

**File:** `src/Rig2Cast.Drivers.Yaesu/Protocol/YaesuAsciiProtocol.cs:318`

```csharp
if (prefix.Length != 2 || ...)
```

FTDX10 responses like `CF001`, `BP01`, `EX030201` have longer distinguishing prefixes. The driver works around this with `ResponsePrefix[..2]`, losing specificity. Any 2-char prefix collision could misroute a response. Elecraft already allows 2–3 chars with `$`; Yaesu should follow.

---

## DESIGN-03 — FTDX10 frequency capability declares `Transmit = false`

**File:** `src/Rig2Cast.Drivers.Yaesu/Ftdx10/Ftdx10Driver.cs:659`

```csharp
new FrequencyRange(30_000, 75_000_000, true, false)
//                                           ^^^^^ wrong
```

The FTDX10 is a transceiver. Any UI reading `RadioCapabilities` to decide whether to show TX controls or validate TX frequency will behave incorrectly.

---

## DESIGN-04 — `ElecraftK3Profile.EncodeMode` is O(n) per call

**File:** `src/Rig2Cast.Drivers.Elecraft/K3Family/ElecraftK3Profile.cs:47`

```csharp
return Modes.Single(pair => pair.Value == mode).Key;
```

Called on every mode set and during observation parsing. Add a static reverse dictionary `IReadOnlyDictionary<RadioMode, char>` alongside `Modes`.

---

## DESIGN-05 — Magic positional indices in `ElecraftK3Driver.ParseInformation`

**File:** `src/Rig2Cast.Drivers.Elecraft/K3Family/ElecraftK3Driver.cs:706`

```csharp
response[28] ... response[29] ... response[30] ... response[32]
```

No comments. Without the K3 Programmer's Reference open, these are unreadable. Named constants or inline comments referencing the field names from the spec are required.

---

## DESIGN-06 — Sub-receiver attenuator double-decode ambiguity

**File:** `src/Rig2Cast.Drivers.Elecraft/K3Family/ElecraftK3Driver.cs:831`

```csharp
"RA$01;" or "RA$10;" => "10db"
```

Two distinct wire codes decode to the same key. Encode path emits only one of them. A radio returning `RA$01;` round-trips to `RA$10` on write — potentially wrong. Clarify against K3 spec.

---

## DESIGN-07 — `RadioDriverObservation` is a nullable property bag

**File:** `src/Rig2Cast.Abstractions/Drivers/RadioDriverObservation.cs`

A single record with 10+ optional fields stands in for a discriminated union. Each `Kind` value silently uses a different subset. Callers in `ApplyObservation` must double-guard on both `Kind` and null checks. Will become unmaintainable when CI-V and binary Yaesu add new frame types. Low priority now; high priority before multi-protocol expansion.

---

## DESIGN-08 — `RadioLeaseManager.Acquire` silently supersedes own lease

**File:** `src/Rig2Cast.Runtime/Leases/RadioLeaseManager.cs:28`

Same owner calling `Acquire` on an active lease issues a new `LeaseToken` (new GUID) and invalidates the previous one. Any code still holding the old token will fail `ValidateCore`. A client that calls `AcquireLeaseAsync` twice to extend duration inadvertently kills its own lease. Should either reject the re-acquire, or renew in place.
