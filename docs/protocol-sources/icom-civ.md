# Icom CI-V protocol sources

## Initial reference model

- Manufacturer/model: Icom IC-7300
- Planned stable Rig2Cast model ID: `icom.ic-7300`
- Protocol family: Icom CI-V
- Default radio address: `94h` (must remain configurable because the operator can change it)
- Default controller address used by PC software: `E0h`
- Validation status: documentation-based and simulated; no physical IC-7300 validation

## Source policy

Official Icom documentation is authoritative. The initial implementation is verified
against the following locally supplied document from Icom's official support site:

- Title: *IC-7300 Full Manual*
- File: `IC-7300_ENG_FM_12b.pdf` (local reference only; not committed)
- Icom publication code/revision: `A7292-4EX-12b`
- Publication date: August 2024
- Relevant sections: Set Mode pages 12-10 and 12-11; Control Command section 19,
  especially pages 19-2, 19-3, and 19-9
- SHA-256: `2A08D85F47FA9CB4297BE290596E4AB9B73C599FD23B97D2BFAF01CDF944A73B`
- Official listing: `https://www.icomjapan.com/lineup/products/145/`
- Verified locally: 2026-09-03

The repository does not redistribute the manual.

Hamlib's Icom backend may be consulted as a secondary behavioral and compatibility
reference. Rig2Cast code and data must remain an original implementation; do not
mechanically translate Hamlib source or copy its model tables. Any behavior learned
only from Hamlib or another implementation must be identified as such and tested or
confirmed against hardware before it is described as physically validated.

## Confirmed family framing used by the foundation

An addressed CI-V frame has the following byte layout:

```text
FE FE <destination> <source> <command and data...> FD
```

`FE FE` is the preamble and `FD` terminates the frame. The initial decoder accepts
additional preamble/fill bytes, arbitrary read fragmentation, concatenated frames,
and unrelated noise while enforcing a maximum encoded-frame length. It deliberately
does not interpret command payloads, BCD frequency values, modes, filters, echo,
address direction, acknowledgements, or transceive behavior. Those belong to the
CI-V session or model profile.

The implemented session correlates only frames whose source and destination reverse
the outbound command and whose message matches the expected command prefix and
driver-supplied validator. Exact outbound echoes are consumed. `FB` is accepted only
by an acknowledgement transaction; `FA` rejects any addressed pending transaction.
All other valid frames are unsolicited. A timeout or abandonment after commitment is
terminal because CI-V has no transaction identifier that could make a late identical
reply safe to reuse.

## Implemented first model slice

The deterministic simulator responds to operating-frequency query `03` with five
little-endian packed-BCD bytes, and mode/filter query `04` with its mode and filter
bytes. It can echo commands, fragment or delay replies, reject or drop the next
command, simulate a close, and broadcast frequency (`00`) or mode (`01`) transceive
frames. It also models acknowledged split and RX/TX mutations and their later state
reads. Section 19 confirms these command and payload formats. USB echo defaults OFF
in the simulator, matching the documented IC-7300 default; tests also exercise echo
ON. These behaviors are `Documented` and `Simulated`, not `Hardware tested`.

Documented operating-mode codes for the first driver are `00` LSB, `01` USB, `02`
AM, `03` CW, `04` RTTY, `05` FM, `07` CW-R, and `08` RTTY-R. Filter codes are `01`
FIL1, `02` FIL2, and `03` FIL3. The standard `[REMOTE]` CI-V baud choices are 4800,
9600, and 19200 or Auto. The independently configured unlinked USB CI-V port also
offers 38400, 57600, and 115200. CI-V Transceive defaults ON; USB Echo Back defaults
OFF.

The IC-7300 driver verifies identity and reads frequency, mode/filter, split, and
RX/TX status. Frequency command `05`, mode command `06`, and split command `0F` are
implemented as acknowledged mutations followed by `03`, `04`, or `0F` readback
verification. Both current-VFO and main-receiver APIs use the same frequency/mode
implementation. VFO selection remains unavailable. Commands `25` and `26` identify
only selected versus unselected VFO data, while command `07` selects A/B but provides
no documented active-VFO query. The driver therefore does not invent a stable A/B
identity that could become stale after reconnect or front-panel changes. PTT command
`1C 00 00/01` is implemented with CI-V acknowledgement. Its callers remain behind
the existing runtime authorization, transmit lease, settled-state verification,
forced-RX, and cleanup path; the driver does not create a parallel transmit path.

The expanded documented/simulated surface includes:

- Adjustable selected filter width through `1A 03`. AM uses indices `00`–`49` for
  200–10,000 Hz in 200 Hz steps. LSB/USB/CW/RTTY and reverse variants use indices
  `00`–`40` for 50–500 Hz in 50 Hz steps followed by 600–3,600 Hz in 100 Hz steps.
  FM FIL1/2/3 is not misreported as the same adjustable passband feature.
- Raw CI-V meters through `15 02` (S meter), `15 11` (power), `15 12` (SWR), and
  `15 13` (ALC). The driver publishes raw 0–255 and normalized 0–1 values but does
  not claim calibrated engineering units.
- Level controls through `14`: AF gain `01`, RF gain `02`, squelch `03`, noise
  reduction level `06`, transmit power `0A`, and noise blanker level `12`.
- Attenuator `11`, preamp `16 02`, AGC `16 12`, and noise blanker/reduction plus
  automatic/manual notch switches `16 22/40/41/48`.
- DATA mode through `1A 06`, coordinated with the base mode command. The current
  abstraction represents DATA LSB, DATA USB, and DATA FM; it has no DATA AM value.
- The radio's shared RIT/Delta-TX offset register through `21 00`, plus independent
  RIT and Delta-TX enables through `21 01` and `21 02`.

Frequency and RIT offset fields use little-endian packed BCD. Level and meter fields
use big-endian packed BCD; the protocol helper exposes both explicitly. The exact
filter-index conversion was cross-checked against the local Hamlib Icom backend as a
secondary compatibility reference after verification of the command and endpoint
codes in the official manual. It remains simulated rather than hardware validated.

## Evidence labels

- `Documented`: behavior traced to an identified official manual revision.
- `Simulated`: covered by deterministic automated fixtures without physical hardware.
- `Hardware tested`: observed on the named radio and firmware; record the setup and
  result separately.

Do not infer `Hardware tested` from Hamlib support or automated fixtures.
