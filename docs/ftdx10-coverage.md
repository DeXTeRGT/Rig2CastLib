# FTDX10 driver coverage

The Yaesu CAT manual is the implementation authority. Hamlib's FTDX10 capability declarations are used as a coverage checklist, not translated code.

## Audit status

Status meanings:

- **Complete**: native capability, driver implementation, simulator, and automated tests exist.
- **Read validated**: complete in software and physically verified without changing radio state.
- **Write validation pending**: setters are fixture-tested but have not all been exercised on hardware.
- **Missing**: no native Rig2Cast contract/implementation yet.

| Native feature | CAT | Software | Physical FTDX10 | Next action |
|---|---|---|---|---|
| Identification | `ID` | Complete | Read validated | None |
| USB automatic information | `AI`, `IF`, `FA`, `FB`, `MD`, `VS`, `ST`, `TX` | Complete, opt-in with confirmation and shutdown cleanup | Enabled and front-panel announcements validated | Continue expanding typed handling for useful announced controls |
| VFO A/B frequency | `FA`, `FB` | Complete | Read validated; interactive setter testing passed | None |
| Active VFO | `VS` | Complete | Read validated; interactive setter testing passed | None |
| Operating mode | `MD` | Complete | Read validated; interactive setter testing passed | None |
| IF passband width | `SH0` | Complete, mode-aware | Read and write validated | None |
| Split | `ST` | Complete | Read validated; interactive setter testing passed | None |
| PTT | `TX` | Complete, lease protected | Read validated | Keep transmit testing explicit/manual |
| AF/RF gain and squelch | `AG0`, `RG0`, `SQ0` | Complete | Read validated | Safely validate setters |
| Mic gain, TX power, processor level | `MG`, `PC`, `PL` | Complete | Read validated | Safely validate setters |
| NR/NB/monitor/VOX/anti-VOX levels | `RL0`, `NL0`, `ML1`, `VG`, `AV` | Complete | Read validated | Safely validate setters |
| IF shift, notch, contour | `IS0`, `BP01`, `CO01` | Complete | Read validated | Safely validate setters |
| Clarifier offset | `CF001` | Complete | Read validated | Safely validate setter |
| NR/NB/notch/contour/APF switches | `NR0`, `NB0`, `BC0`, `BP00`, `CO00`, `CO02` | Complete | Read validated | Safely validate setters |
| Monitor/processor/VOX/lock/break-in | `ML0`, `PR0`, `VX`, `LK`, `BI` | Complete | Read validated | Safely validate setters |
| RIT/XIT switches | `RT`, `XT` | Complete | Read validated | Safely validate setters |
| Tuner state | `AC` | Complete | Read validated | Keep tuner-start as a separate hazardous action |
| Attenuator/preamp/AGC | `RA0`, `PA0`, `GT0` | Complete | Read validated | Safely validate setters |
| Roofing filter | `RF0` | Complete | Read validated | Safely validate setters; optional 300 Hz filter is capability-sensitive |
| Raw meters | `SM0`, `RM3`-`RM8` | Complete, uncalibrated | Read validated | Add independently validated engineering calibration later |
| CW pitch | `KP` | Complete, typed Hz control | Not tested | Physically validate read and safe setter |
| Keyer speed | `KS` | Complete, typed WPM control | Not tested | Physically validate read and safe setter |
| VOX delay | `VD` | Complete, discrete `off`/100-3000 ms choices | Not tested | Physically validate read and safe setter |
| APF frequency/width | `CO03`, `EX030201` | Complete, typed offset and width choice | Not tested | Physically validate in CW mode |
| Tuning step | `FS` | Complete, mode-aware normal/fast choices | Not tested | Physically validate read and safe setter |
| Repeater offset/shift and tones | manual repeater/tone controls | Missing | Not tested | Defer until core HF controls are complete |
| Memories, scan, QMB, band operations | multiple | Missing | Not tested | Later milestone |
| CW/voice message operations | multiple | Missing | Not tested | Later milestone |
| Power, clock and information | multiple | Missing | Not tested | Later milestone |

The audit shows that the previously proposed first control batch (gain, squelch,
power, microphone, clarifier, noise reduction, notch, contour and raw meters) is
already implemented natively. The immediate missing-control batch is now complete:
CW pitch, keyer speed, VOX delay, APF parameters, and mode-aware tuning step.

## Implemented command summary

| Area | CAT commands |
|---|---|
| Identification | `ID` |
| USB automatic information | `AI` |
| VFO A/B frequency | `FA`, `FB` |
| Active VFO | `VS` |
| Operating mode | `MD` |
| Split | `ST` |
| CAT PTT and PTT status | `TX` |
| Numeric controls | `AG0`, `RG0`, `SQ0`, `MG`, `PC`, `PL`, `RL0`, `NL0`, `ML1`, `VG`, `AV`, `KP`, `KS`, `CO03` |
| Raw meters | `SM0`, `RM3`, `RM4`, `RM5`, `RM6`, `RM7`, `RM8` |
| Switch controls | `NB0`, `NR0`, `ML0`, `PR0`, `VX`, `LK`, `BI`, `AC` |
| Choice controls | `RA0`, `PA0`, `GT0`, `VD`, `EX030201`, `FS` |
| Receiver filtering/interference controls | `IS0`, `NA0`, `BC0`, `BP00`, `BP01`, `CO00`, `CO01`, `CO02`, `RF0` |
| Mode-aware filter width | `SH0` |
| Receive/transmit clarifier and signed offset | `RT`, `XT`, `CF001` |

The capability descriptor publishes each implemented control's range, step, unit, and access support. Meter readings expose the manual-defined raw value and a normalized 0.0-1.0 value. They deliberately do not claim calibrated S-units, watts, SWR, amperes, or volts yet.

## Planned Hamlib parity

- Tuner-start action and detailed CW break-in configuration
- Repeater shift/offset and CTCSS
- Memory channels, copy/swap, QMB, scan, band and tuning operations
- CW keyer, key speed/pitch, Morse sending, and voice memories
- Power status, clock, and information queries

Capabilities remain `DriverNotImplemented` or absent until their typed APIs, command parsing, and tests exist. This prevents a generic client from enabling controls that the driver cannot safely execute.

## Serial profile

The FTDX10 CAT profile uses 8 data bits, no parity, two stop bits, and hardware flow control. Supported CAT baud rates are 4800, 9600, 19200, and 38400 baud. Port and baud rate are deployment configuration; the local hardware test profile is `COM11` at 38400 baud.
