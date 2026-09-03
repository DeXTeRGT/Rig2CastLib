# Xiegu G90 CI-V protocol record

The implementation source is the local `XIEGU RADIO CI-V REFERENCE`, version 1.0:

- local file: `tcvr_manuals/XIEGU-RADIO-CI-V-REFERENCE.pdf`
- SHA-256: `51CECFF85563298CCD6A961F877ADEE2D8E903D112E1A20425EDD552C6A06E60`
- status: vendor reference supplied by the user; the PDF is local evidence and must
  not be committed

The initial driver uses standard CI-V framing and the documented commands `03`/`05`
(current frequency), `04`/`06` (current mode),
`0F` (split), and `1C 00` (RX/TX status and control). Address `70`, controller `E0`,
and 19200 baud are the initial profile defaults. Mode codes are LSB `00`, USB `01`,
AM `02`, CW `03`, NFM `05`, and CW-R `07`.

The table leaves the `Rigs (Note 1)` cell blank for `19 00`; Note 1 defines blank as
all Xiegu radios. Nevertheless, the standard `19 00` identity query timed out on the
user's physical G90 and is not used as an opening requirement. This is a recorded
manual-versus-hardware discrepancy. The Xiegu-specific `1D 19` query and G90/G90S
response code `00 90` are the primary opening probe and were physically validated by
the user. Firmware 1.80 is the minimum for positive model identification. If older
firmware times out or rejects `1D 19`, the driver replaces the terminal session and
uses read-only frequency query `03` as a compatibility probe, explicitly marking the
identity unverified. Command `04` parsing accepts a mode-only
response or a trailing filter byte to tolerate documented/vendor-family variants.

The next implemented documented surface is `14 01/02/06/0A/0B/12/15/17` for AF,
RF, NR, TX-power, microphone, NB, monitor, and anti-VOX levels; raw meters
`15 02/11/12/13`; attenuator `11`; preamp `16 02`; AGC `16 12`; NB `16 22`;
speech compressor `16 44`; read-only dial lock `16 50`; tuner enabled/bypassed
state `1C 01`; and RIT offset plus RIT/XIT enables `21 00/01/02`. The exact G90
access asymmetry is retained where possible. Attenuator writes are implemented
idempotently around the radio's toggle command: read, toggle only when necessary,
then verify. Physical firmware 1.81 applies the toggle without returning `FB`, so the
state readback—not an acknowledgement—is the success criterion. The physical radio
returns `11 00` when off and `11 0C` when active; the driver consequently maps zero
to off and a nonzero attenuation value to on. Tuner start is
deliberately not exposed; `AntennaTuner` represents only
the enabled/bypassed state. Physical firmware 1.81 returned `00 4D` for AF,
`01 2E` for RF gain, and `02 5F` for transmit power at 20 W. Invalid BCD nibbles and
values over 255 prove that this firmware uses a two-byte binary read representation,
contrary to the table's BCD `0000–0255` statement. Maximum AF volume and maximum
20 W transmit power both physically return `02 5F`, binary 607, establishing the
command `14` full-scale endpoint. Reads are therefore exposed as uncalibrated binary
raw 0–607 values. All `14` writes are temporarily disabled until
physical captures establish safe read/write conversion. RIT uses its separately
specified encoding and remains writable.

This observation is a G90 command/firmware behavior, not a general CI-V encoding
rule. CI-V payload encoding is command- and model-specific: frequency and RIT fields
in this same protocol use packed BCD, while the physically captured G90 `14` level
responses use two-byte big-endian binary. Do not infer binary encoding for another
command or radio without documentation or byte captures.

Physical firmware 1.81 VFO captures establish the G90-specific `25`/`26` behavior:

- bare `25` and bare `26` return NAK (`FA`);
- request selector `00` addresses the foreground VFO and `01` the background VFO;
- the selector in a successful response identifies the absolute active VFO (`00` A,
  `01` B), rather than echoing the request selector;
- an isolated tool may appear to time out if it rejects this non-echoed selector;
- reads and writes of both frequencies were physically confirmed, including writing
  background VFO B to 7.060 MHz while VFO A remained active;
- `07 00`/`07 01` select absolute VFO A/B and `07 A0`/`07 B0` equalize/exchange them;
- `26` DATA mode was physically confirmed, with the current filter byte preserved;
- split correctly routes receive to the active VFO and transmit to the other VFO.

The driver therefore correlates G90 `25`/`26` replies by command and validates their
payload independently of the request selector. It performs the foreground/background
reads close together, reconstructs stable absolute A/B state from the returned active
selector, and advertises A/B only after the extended probe succeeds. Firmware that
rejects or ignores the extension retains the conservative current-VFO profile.
Recovery closes and reopens the transport before awaiting disposal of a timed-out
CI-V session. This is required because some Windows serial drivers leave
`SerialPort.BaseStream.ReadAsync` blocked despite cancellation; awaiting that reader
before closing the port can otherwise strand Console startup indefinitely.

Still deferred are public equalize/exchange operations, direct background-mode and
filter/passband APIs, command `14` writes, CW sidetone/key-speed conversions, QSK
time, LCD backlight, squelch-gate status, supply-voltage semantics, and tuner start.
They need calibration, a suitable public abstraction, or additional safety semantics.
The reference marks noise-reduction and VOX switch commands as X6100-only, so they
must not be advertised by the G90 profile.

Physical firmware 1.81 correctly reports speech-compressor state with `16 44` and
returns `FB` for documented writes `16 44 00/01`. During testing, however, those
writes did not produce a visible change on the transceiver. The read/write capability
is retained because the command is documented and acknowledged, but this remains a
firmware-behavior caveat rather than a physically confirmed mutation.

Physical firmware 1.81 testing confirmed that `1C 01` correctly reads and changes
the tuner enabled/bypassed state. This validation does not cover tuner start, which
remains deliberately unavailable through the current public API.

Physical AGC testing established `16 12 01` as FAST, `16 12 02` as SLOW, and
`16 12 03` as AUTO. The G90 profile consequently exposes `fast`, `slow`, and `auto`;
the earlier assumed `medium` label was incorrect and has been removed.

Physical firmware 1.81 testing also confirmed preamp read/write (`16 02`), noise
blanker read/write (`16 22`), signed clarifier offset read/write (`21 00`), and the
independent receive/transmit clarifier switches (`21 01`/`21 02`). Console readback
and the corresponding front-panel state agreed.
