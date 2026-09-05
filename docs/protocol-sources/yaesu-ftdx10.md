# Yaesu FTDX10 protocol sources

## Implementation authority

- Document: `tcvr_manuals/FTDX-10_CAT_user.pdf`
- Yaesu document revision: `2308-F`
- SHA-256: `E78AC5B49A2C04A0A79CB541857389765D485866980A41EE05ED133DE92F0B05`
- Official publication: Yaesu FTDX10 CAT Operation Reference Manual

Command syntax, parameters, response parsing, identification value `0761`, serial framing, and radio behavior are implemented from this manual.

## Coverage checklist

- Repository: sibling `Hamlib` checkout
- Commit inspected: `da76639d663b82589d3b2526eb7f0d68148a55f0`
- Files inspected for declared coverage: `rigs/yaesu/ftdx10.c`, `rigs/yaesu/ftdx10.h`

Hamlib is used to identify the feature categories users currently expect. Its functions, tables, comments, calibration curves, and implementation are not translated into Rig2Cast. Values that are not specified in the Yaesu manual—particularly meter calibration—require independent hardware validation before implementation.

### Independently verified protocol deviation

The manual revision above documents the `PR` speech-processor state as `1` for off and `2` for on. The physical FTDX10 returned `PR00;` for the off state on 2026-08-30. Rig2Cast therefore uses the hardware-observed `0`/`1` encoding. The sibling Hamlib source was consulted only after observing the discrepancy and independently corroborates that related Yaesu models deviate from their manuals in the same way.

## Validation status

- Yaesu ASCII framing: automated fixture tests
- Identification, frequency, VFO, mode, split, and PTT commands: automated fixture tests
- Numeric controls and raw meter parsing: automated fixture tests
- Switch and choice control parsing: automated fixture tests
- Receiver filtering and interference command parsing: automated fixture tests
- Mode-aware filter-width and signed clarifier parsing: automated fixture tests
- CW pitch, keyer speed, discrete VOX delay, APF parameters, and mode-aware tuning step: automated fixture tests
- Simulator/runtime integration: automated tests
- Physical FTDX10 validation: passed on 2026-08-30 using the Enhanced CAT port at `COM11`, 38400 baud
  - Identification response verified as `ID0761;`
  - `FA`, `FB`, `VS`, `MD`, `ST`, and `TX` read/query paths verified
  - `SM0` and `RM3` through `RM8` read/query paths verified; idle raw values were `0` except drain voltage (`192`)
  - `NB0`, `NR0`, `ML0`, `PR0`, `VX`, `LK`, `BI`, and `AC` switch queries verified
  - `RA0`, `PA0`, and `GT0` choice queries verified; reported values were attenuator off, preamp AMP 1, and AGC auto-slow
  - `IS0`, `NA0`, `BC0`, `BP00`, `BP01`, `CO00`, `CO01`, `CO02`, and `RF0` read/query paths verified
  - Reported filtering state was IF shift 0 Hz, manual-notch frequency 1500 Hz, contour frequency 1500 Hz, and 3 kHz roofing filter; all related switches were off
  - `SH0`, `RT`, `XT`, and `CF001` read/query paths verified
  - In USB mode the radio reported 3000 Hz filter width, RIT and XIT off, and 0 Hz clarifier offset
  - Reported state was receive, VFO A selected, USB mode, and split off
  - No set, tuning, or transmit command was sent during validation
- Subsequent interactive hardware validation:
  - VFO frequency, active-VFO, mode, and split setters passed through the diagnostic console
  - Full state reads now use the manual's VFO-qualified information commands: `IF`
    for VFO A and `OI` for VFO B. Their independent modes are retained and the
    active mode is selected through `VS`. `MD0`/`MD1` identify MAIN/SUB bands and
    are not treated as direct A/B identities in unsolicited observation handling.
  - Physical tracing established that `MD0` follows the foreground/operated VFO and
    `MD1` the background/opposite VFO. During selection the new `MD0`/`MD1` pair may
    precede `VS1`, so mapping it through the previously cached selection is unsafe.
    An `MD0` announcement therefore requests a serialized authoritative `IF`/`OI`/`VS`
    state refresh; `MD1` is recognized but does not directly mutate state.
  - On 2026-08-31, rigctld mode/passband reads and atomic setters passed; native `SH0` width selection was confirmed on the physical radio
  - On 2026-08-31, automatic-information traffic was observed to emit undocumented
    `FDxxx#########;` frames only when VFO tuning reached a visible spectrum edge and
    caused the scope to scroll. The embedded frequency remained exactly 5 kHz below
    VFO A in the captured 10 kHz-span example, proving it was not a VFO frequency.
    Valid frames of this shape are recognized and ignored to avoid diagnostic floods;
    malformed `FD` frames remain diagnostics. This behavior is hardware-observed and
    is not claimed as an officially documented CAT command.
