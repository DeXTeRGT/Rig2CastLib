# Elecraft K3-family protocol sources

## Implementation authority

- Document: `tcvr_manuals/K3S&K3&KX3&KX2 Pgmrs Ref, G5.pdf`
- Elecraft revision: G5
- SHA-256: `015B60C1B5F44346247A00375A23174012CE4DA66A6A27EAA00C00C20877F95D`
- Official publication: Elecraft K3S/K3/KX3/KX2 Programmer's Reference

The implementation is based on the manufacturer-published command definitions and
fixed-width response formats. Hamlib source code has not been translated into this
driver.

## Initial implemented slice

- Selectable model identities: `elecraft.k3s`, `elecraft.k3`, `elecraft.kx3`, and
  `elecraft.kx2`
- Model verification using `OM`
- Configurable 4800, 9600, 19200, and 38400 baud, with 38400 as the default
- VFO A/B frequency read and write using `FA` and `FB`
- Operating-mode read and write using `IF`, `MD`, and `MD$`
- Explicit split/transmit-VFO control using `FT`; split cancellation using `FR0`
- PTT-state read using `TQ`; PTT write using `TX` and `RX`
- AI1 unsolicited `IF`, `FA`, `FB`, `MD`, `FT`, and `TQ` observations
- Explicit `?;` command-rejection handling without treating the serial session as lost
- Capability discovery for the implemented core; FM is excluded from the KX2 profile
- AF gain (`AG`) and RF gain (`RG`)
- Requested transmit power (`PC`), with the connected `OM` option response used to
  distinguish the 110 W amplifier range from the low-power range
- RIT/XIT offset (`RO`) and RIT/XIT state (`RT`/`XT`)
- AGC speed (`GT`)
- Model-specific attenuator values (`RA`)
- Preamplifier selection (`PA`), including preamp 2 only when `OM` announces the
  band-dependent LNA option
- Mode-dependent numeric passband discovery and `BW` read/write in 10 Hz units;
  capability metadata explicitly warns that the radio may quantize the request
- K3/K3S high-resolution raw S-meter (`SMH`), KX3/KX2 basic raw S-meter (`SM`),
  and protocol-defined tenths-of-a-unit SWR (`SW`)
- Typed unsolicited numeric, switch, choice, and passband observations for the
  implemented control commands
- `$`-suffixed AF/RF gain announcements retain a typed VFO B/sub-receiver target;
  main and sub values are never merged. The native API also supports explicit
  receiver targeting for `AG$`, `RG$`, `RA$`, `PA$`, `BW$`, and `SM$` when the
  connected K3/K3S `OM` response announces an installed sub receiver. Capability
  metadata exposes target-specific choices and meter ranges.
- Keyer speed (`KS`, 8-50 WPM) read/write and typed AI2 observation
- Configurable AI1/AI2/AI3 selection; AI2 is used for typed front-panel control
  responses while AI1 retains consolidated `IF` state behavior. Automatic-information
  sessions explicitly select K3 extended command mode (`K31`) so legacy `FW`
  layouts cannot be mistaken for filter bandwidth.

The K3-family `FR` behavior is not represented as ordinary active receive-VFO
selection because the manual defines it in relation to split cancellation. The
capability is therefore reported as unsupported rather than offering a misleading
generic operation.

## Validation status

- Protocol framing, routing, parsing, model discrimination, writes, AI1 observations,
  and command rejection: automated fixture tests
- Host model discovery and build integration: verified
- Physical K3S core validation: passed on 2026-09-01 at 38400 baud
  - Model verification, VFO A/B frequency, operating mode, split/transmit VFO,
    unsolicited state reporting, and clean shutdown were confirmed
  - AF/RF gain, requested power, RIT/XIT offset and state, AGC speed, attenuator,
    and preamp controls passed interactive validation on 2026-09-01
  - Shared passband/`BW` native and rigctld control passed interactive validation
    on 2026-09-01, including radio readback/quantization behavior
  - AI2 main-receiver AF/RF gain announcements passed physical validation on
    2026-09-01; physical captures also confirmed `AG$`/`RG$` as sub-receiver values
    and `KS` as keyer speed
  - S-meter, SWR, and the remaining typed control announcements still require
    interactive validation
- K3, KX3, and KX2 hardware validation: not yet performed

Only the explicitly listed physical checks are hardware-verified; remaining features
retain documented-protocol plus automated-fixture status until tested on the radio.

## Bandwidth representation

The official `BW` command represents a nominal 0-99990 Hz range in 10 Hz units,
but the radio quantizes and limits it according to operating mode. Rig2Cast models
this as a mode-dependent numeric passband with a 10 Hz request step and an explicit
`RadioMayQuantize` indication. FTDX10 uses the same abstraction with discrete
mode-specific values.
