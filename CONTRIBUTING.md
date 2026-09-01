# Contributing

Contributions must be original or compatible with `AGPL-3.0-only`. Contributors must document protocol sources and must not mechanically translate Hamlib code or data tables.

Every feature and bug fix must include automated tests. Driver changes must cover command encoding, valid response parsing, malformed or rejected responses, capability publication, and applicable model or firmware boundaries. Runtime changes must cover authorization, serialization, cancellation, failure recovery, and cleanup where relevant.

Hardware tests must be explicit and disabled by default. Record real-radio model and firmware validation separately from simulated validation; an automated fixture does not by itself establish hardware support.

Do not commit transceiver manuals or other copyrighted reference documents without explicit redistribution permission.
