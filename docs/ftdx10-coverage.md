# FTDX10 driver coverage

The Yaesu CAT manual is the implementation authority. Hamlib's FTDX10 capability declarations are used as a coverage checklist, not translated code.

## Implemented

| Area | CAT commands |
|---|---|
| Identification | `ID` |
| VFO A/B frequency | `FA`, `FB` |
| Active VFO | `VS` |
| Operating mode | `MD` |
| Split | `ST` |
| CAT PTT and PTT status | `TX` |
| Numeric controls | `AG0`, `RG0`, `SQ0`, `MG`, `PC`, `PL`, `RL0`, `NL0`, `ML1`, `VG`, `AV` |
| Raw meters | `SM0`, `RM3`, `RM4`, `RM5`, `RM6`, `RM7`, `RM8` |
| Switch controls | `NB0`, `NR0`, `ML0`, `PR0`, `VX`, `LK`, `BI`, `AC` |
| Choice controls | `RA0`, `PA0`, `GT0` |
| Receiver filtering/interference controls | `IS0`, `NA0`, `BC0`, `BP00`, `BP01`, `CO00`, `CO01`, `CO02`, `RF0` |
| Mode-aware filter width | `SH0` |
| Receive/transmit clarifier and signed offset | `RT`, `XT`, `CF001` |

The capability descriptor publishes each implemented control's range, step, unit, and access support. Meter readings expose the manual-defined raw value and a normalized 0.0-1.0 value. They deliberately do not claim calibrated S-units, watts, SWR, amperes, or volts yet.

## Planned Hamlib parity

- Tuning steps
- APF frequency offset, tuner-start action, and detailed CW break-in configuration
- Repeater shift/offset and CTCSS
- Memory channels, copy/swap, QMB, scan, band and tuning operations
- CW keyer, key speed/pitch, Morse sending, and voice memories
- Power status, clock, and information queries

Capabilities remain `DriverNotImplemented` or absent until their typed APIs, command parsing, and tests exist. This prevents a generic client from enabling controls that the driver cannot safely execute.

## Serial profile

The FTDX10 CAT profile uses 8 data bits, no parity, two stop bits, and hardware flow control. Supported CAT baud rates are 4800, 9600, 19200, and 38400 baud. Port and baud rate are deployment configuration; the local hardware test profile is `COM11` at 38400 baud.
