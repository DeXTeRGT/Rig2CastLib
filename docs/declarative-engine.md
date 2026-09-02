# Declarative engine foundation

The declarative layer is a collection of immutable, strongly typed protocol data
descriptors. It is not a replacement for a protocol session, driver, or managed-radio
safety policy. Descriptors may define regular wire values and command metadata;
framing, correlation, addressing, pacing, recovery, and leases remain executable C#.

## Version-1 vocabulary freeze

`DeclarativeDescriptorVocabulary.CurrentVersion` is 1. Version 1 consists of
`ValueMapDescriptor`, `NumericFieldDescriptor`, `AsciiQueryDescriptor`,
`AsciiQuerySet`, `ModeApplicabilityDescriptor`, and
`ConditionalValueSetDescriptor` plus their value records. Their documented
construction validation and lookup semantics are frozen for the driver API 1.0
cycle. Additive types remain possible, but existing semantics should not change
without a driver API compatibility decision and regression tests.

This freeze covers compiled C# descriptors only. It does not define a serialized
schema, automatic command executor, framing engine, or promise that arbitrary radio
behavior can be expressed declaratively.

## Existing declaration inventory

The FTDX10 implementation already contains data-shaped private records for numeric
controls, meters, switches, choices, choice codes, mode-dependent filter widths, and
tuning steps. These repeat command/query prefixes, response lengths, digits,
minimum/maximum/scale values, boolean codes, display metadata, and mode applicability.
They are strong candidates for later validated descriptors because their behavior is
regular and their capability metadata is derived from the same values.

The Elecraft K3-family implementation repeats command selection and encode/decode
switches for numeric controls, switches, AGC, attenuator, and preamp choices. It also
contains model/option gates for the sub receiver, second preamp, power limits, FM,
and firmware-gated SWR. The regular maps and numeric ranges are candidates. Option
discovery, model matching, firmware parsing, secondary-receiver topology, and
conditional capability construction should remain explicit C# escape hatches.

Both families contain bijective CAT mode-code mappings. This is the first pilot.
`ValueMapDescriptor<TWire,TValue>` validates non-empty, unambiguous mappings once,
freezes both directions, and provides constant-time encode/decode lookup. FTDX10 and
Elecraft retain their existing public `Modes` views and error behavior, so this is a
data/validation refactor rather than a protocol behavior change.

## Boundaries

Suitable declarative data:

- command and response identifiers;
- fixed widths and numeric range/step/scale rules;
- unambiguous wire-to-domain value maps;
- supported targets and mode applicability;
- capability display metadata;
- explicit model, option, or firmware predicates supplied as C# policy hooks.

Keep in protocol or driver code:

- ASCII framing and response correlation;
- CI-V addressing, echo, collision, ACK/NAK, and resynchronization;
- legacy Yaesu binary transaction and status-block state machines;
- composite reads/writes and conditional query sequences;
- connection-failure classification and recovery;
- PTT authorization, transmit leases, and other safety policy.

## Incremental roadmap

1. Complete: validated bidirectional value maps piloted on both mode tables.
2. Complete: `NumericFieldDescriptor`, `AsciiQueryDescriptor`, and
   `AsciiQuerySet<TKey>` validate unsigned widths/ranges/steps, response envelopes,
   duplicate keys/commands, and identical or overlapping response prefixes.
3. Complete for the first pilot: FTDX10 read-only meter declarations now drive query
   metadata, numeric parsing, and capability ranges from the same descriptors.
   Existing golden frames, reserved-suffix filtering, normalization, timestamps, and
   malformed-response behavior are preserved. The `RM` reserved suffix remains an
   explicit Yaesu-family validator because it is not generic numeric-field behavior.
4. Complete cross-vendor check: the Elecraft keyer-speed read query uses the same
   ASCII/numeric descriptors for its command, case-insensitive response envelope,
   parsing bounds, and capability range. Its protocol-specific exception remains in
   the Elecraft driver. This confirms that response comparison is declared rather
   than accidentally imposing Yaesu casing rules on another family.
5. Complete mode-applicability pilot: `ModeApplicabilityDescriptor<TValue>` validates
   supported-mode coverage, empty applicability, duplicate values, unsupported mode
   references, and optional per-mode value counts. FTDX10 tuning steps now use one
   ordered declaration for normal/fast selection and generated capability options;
   the existing `MD`/`FS` multi-command sequence stays explicit driver code.
6. Complete conditional hook pilot: `ConditionalValueSetDescriptor<TContext,TValue,TWire>`
   validates unique domain/wire values and evaluates typed C# availability predicates.
   Elecraft main-receiver preamp encode, decode, and capability options now share one
   declaration evaluated against model and discovered-option context. Sub-receiver
   preamp behavior remains explicit because it has a separate command and limits.
7. Add target applicability only when required by a concrete receiver-targeted pilot.
8. Use the descriptor vocabulary while designing CI-V and legacy Yaesu binary
   engines, but keep their distinct framing/state machines separate.
9. Consider versioned external JSON/YAML only after the typed C# model has survived
   multiple protocol families. Do not add scripts or executable expressions.

Success is measured by fewer duplicated declarations and earlier validation without
making exceptional radio behavior harder to express or changing wire behavior.

The independent
[`Rig2Cast.DeclarativeExamplePlugin`](../samples/Rig2Cast.DeclarativeExamplePlugin/README.md)
shows all version-1 descriptor categories in a plugin that can be loaded and exercised
through the diagnostic Console.
