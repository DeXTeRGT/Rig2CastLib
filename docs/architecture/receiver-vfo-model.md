# Receiver and VFO separation

Status: additive migration in progress. Receiver identity, topology capabilities,
parallel receiver/VFO state, and receiver-targeted frequency, mode, controls,
switches, choices, passband, and meters are implemented. Legacy VFO APIs remain available while the
new receiver-targeted operations are physically validated and in-tree consumers
are migrated.

## Problem

`VfoId` currently mixes receiver identities (`Main`, `Sub`), tuning registers
(`A`, `B`, `Memory`), and the contextual alias `Current`. These concepts happen
to overlap on the first supported radios, but they are independent on radios
with multiple receivers, selectable VFOs per receiver, diversity, dual watch,
or dynamically created slices.

Elecraft's `$` syntax demonstrates the distinction: it addresses VFO B or the
sub receiver depending on the command and radio configuration. That CAT syntax
must be interpreted by the driver rather than copied into the common model.

## Proposed identities

Receiver identity must be extensible rather than a closed enum:

```csharp
public readonly record struct ReceiverId(string Value)
{
    public static ReceiverId Main => new("main");
    public static ReceiverId Sub => new("sub");
}
```

Drivers may publish additional stable values such as `receiver-3` or `slice-0`.
The capability document is authoritative for the receivers available in a
particular connected-radio configuration.

`VfoId` remains the identity of a tuning register or source. Its long-term
generic values are `A`, `B`, and `Memory`. `Current` remains only as a command
addressing convenience and must not appear as stored state. `Main` and `Sub`
become compatibility-only aliases during migration and are removed in the next
major public API.

## State model

The canonical state becomes receiver-oriented:

```csharp
public sealed record RadioReceiverState(
    ReceiverId Receiver,
    VfoId? SelectedVfo,
    long FrequencyHz,
    RadioMode Mode,
    int? PassbandHz,
    DateTimeOffset ObservedAt);

public sealed record RadioVfoState(
    VfoId Vfo,
    long FrequencyHz,
    RadioMode? Mode,
    DateTimeOffset ObservedAt);
```

`RadioState` publishes receiver states and, where the radio exposes persistent
VFO registers independently, VFO states. A driver is not required to invent a
VFO register for a receiver-oriented or slice-oriented protocol.

Split state must identify the receive and transmit signal paths explicitly.
It must not infer the transmit VFO by selecting the opposite of the receive
VFO in common runtime code.

## Capability model

Capabilities publish:

- available receiver identities and their roles;
- whether a receiver supports independent tuning, mode, passband, controls,
  meters, and VFO selection;
- the VFO registers available to each receiver;
- diversity, dual-watch, and sub-receiver relationships as optional features;
- target-specific limits and choices.

Target-aware controls, choices, passband, and meters migrate from `VfoId target`
to `ReceiverId receiver`. Frequency operations retain an explicit VFO overload
where the hardware exposes VFO registers and gain a receiver-targeted form for
radios whose protocol tunes receiver paths directly.

## Initial driver mappings

### Yaesu FTDX10

- Publish receiver `main`.
- Publish VFO registers A and B.
- Map current frequency/mode/passband state to receiver `main` and its selected
  VFO.
- Keep split transmit mapping as explicit driver state; do not generalize the
  FTDX10 opposite-VFO behavior.

### Elecraft K3/K3S

- Always publish receiver `main`.
- Publish receiver `sub` only when the `OM` response announces the sub-receiver
  option.
- Map ordinary receiver controls to `main` and `$` receiver controls to `sub`.
- Preserve A/B as VFO registers. Command-specific mapping decides whether `$`
  means VFO B, sub receiver, or dual-watch context.

### Elecraft KX3/KX2

- Do not infer a physical sub receiver from shared command syntax.
- Publish only verified receiver topology and command targets for each model.

### rigctld

- Continue exposing Hamlib VFO names through an adapter mapping.
- Do not let rigctld terminology constrain the canonical native model.
- Return `ENIMPL`/the appropriate rigctld failure when a native topology cannot
  be represented safely.

## Compatibility migration

1. Add `ReceiverId`, receiver descriptors, and receiver state alongside the
   existing API.
2. Populate both representations in FTDX10, K3-family, and simulator drivers.
3. Add receiver-targeted session and operation-scope methods.
4. Move Console and future REST/gRPC contracts to the receiver-oriented API.
5. Keep existing `VfoId` target methods as adapters for one compatibility cycle.
6. Mark `VfoId.Main` and `VfoId.Sub` obsolete only after all in-tree consumers
   have migrated.
7. Remove ambiguous aliases in the next major API version.

Compatibility adapters must fail explicitly when a legacy call is ambiguous;
they must not silently choose a receiver.

## Driver plug-in implications

The receiver model and capability schema form part of the driver SDK contract.
They must stabilize before enabling third-party assembly discovery. Plug-in
loading should later validate driver API version, model identifiers, declared
transports, and capability documents before a driver can be selected. Discovery
and trust policy remain host concerns; radio protocol assemblies should not need
references to REST, gRPC, rigctld, or application projects.

## Required tests before migration completion

- One receiver with VFO A/B (FTDX10).
- Main/sub receivers with A/B semantics (K3S).
- Model configuration without an installed sub receiver.
- Independent receiver control and meter targeting.
- Split receive/transmit path mapping.
- Legacy VFO adapter success and explicit ambiguity failures.
- Capability serialization round trips for stable receiver identifiers.
- A synthetic driver with at least three receivers or slices to prove the model
  is not limited to Main/Sub.
