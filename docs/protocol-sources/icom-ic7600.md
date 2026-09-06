# Icom IC-7600 CI-V protocol record

## Reference and evidence

- Manufacturer/model: Icom IC-7600
- Stable Rig2Cast model ID: `icom.ic-7600`
- Protocol family: Icom CI-V
- Default radio address and model identity: `7Ah`
- Default controller address: `E0h`
- Validation status: documented and simulator-tested; physical validation pending
- Official source: *IC-7600 Instruction Manual*, file `IC-7600_ENG_4.pdf`
  (local reference only; not committed)
- Relevant pages: command table, PDF pages 169-174
- SHA-256: `5FF4418A1A39D567F29097ACC55AFC86843E244403D032096C014955A06891B7`
- Verified locally: 2026-09-07

Official Icom documentation is authoritative. Hamlib or another implementation may
only be used as a secondary behavioral reference. The repository does not
redistribute the manual.

## Initial driver slice

The initial driver is intentionally read-only. On opening it sends identity query
`19 00` and requires model identity `7A`, independently of the configurable CI-V
destination address. It then reads:

- Main and Sub frequency with `25 00` and `25 01`.
- Main and Sub mode, DATA selection, and filter with `26 00` and `26 01`.
- Selected Main/Sub readout with `07 D2`.
- Dualwatch state with `07 C2`.
- Split state with `0F`.
- Receive/transmit state with `1C 00`.

The public state models Main and Sub as receivers. It does not invent persistent VFO
A/B identities: the documented `25` and `26` selectors address the Main and Sub
readouts directly. Main is the normal transmit path; split routes transmit through
Sub. Dualwatch makes both receive paths active. The radio supports independent Main
and Sub frequency and mode, subject to the radio's own dualwatch restrictions.

Initial modes are LSB (`00`), USB (`01`), AM (`02`), CW (`03`), FM (`05`), and CW-R
(`07`). DATA 1/2/3 applied to LSB, USB, or FM maps to the existing `DataLsb`,
`DataUsb`, or `DataFm` abstraction. RTTY, RTTY-R, PSK, and PSK-R are deliberately
deferred. The documented receive range is 30 kHz through 60 MHz with 1 Hz command
resolution.

The serial profile is 8 data bits, no parity, one stop bit, and no handshake. The
published baud choices are exactly 300, 1200, 4800, 9600, and 19200; the driver does
not advertise rates above the radio's documented 19200 baud maximum.

Frequency, mode, receiver selection, dualwatch, split, PTT, passband, controls, and
meters remain read-only or unavailable for mutation in this first slice. Mutation
will be added in reviewed batches with acknowledgement, authoritative readback, and
the existing runtime transmit-lease protections where applicable.

