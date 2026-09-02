# Architecture

Rig2Cast has one runtime and two first-class deployment forms: embedded in a .NET process or hosted by a standalone service. Network adapters contain no radio-control logic.

## Dependency direction

```text
Applications / REST / WebSocket / gRPC / rigctld
                       |
                    Runtime
          sessions, leases, scheduling, state
                       |
             abstractions and domain types
                       |
       driver -> protocol -> transport -> radio
```

One managed session exclusively owns each physical connection. Logical clients share that session and never write directly to a transport. PTT always requires a transmit lease.

## Protocol engines and family policies

`Rig2Cast.Protocols` contains reusable wire-session infrastructure. Its ASCII CAT
engine owns serialized commands and queries, the continuous read loop, frame
accumulation, response correlation, unsolicited-frame delivery, bounded overflow
reporting, timeouts, terminal faults, cancellation, and disposal.

Caller cancellation is harmless only before a query frame is committed. Once a
complete query has been written, abandoning its response makes correlation
ambiguous; the ASCII session becomes terminal and must be replaced. This uses the
same conservative recovery policy as a response timeout and prevents a late reply
from satisfying a later same-prefix query.

The engine is configured by a protocol-family policy. Yaesu and Elecraft retain thin
family-specific facades that define command framing, valid response prefixes,
protocol exceptions, and command-rejection behavior. Radio-model parsing remains in
the driver and is not moved into the shared engine.

This is intentionally not a universal radio protocol base. Future ASCII command
families can reuse the ASCII engine when their wire behavior fits its contract.
Binary protocols such as Icom CI-V, and network-native radio APIs, should use separate
engines while continuing to implement the same radio-driver abstractions.

## Driver plugins

External drivers use the same `IRadioDriverFactory` contract and catalog as built-in
drivers. The host owns sidecar-manifest discovery, exact driver-API compatibility,
SHA-256 trust decisions, safe entry paths, duplicate detection, diagnostics, and
collectible assembly-load contexts. Manifest model metadata is checked against the
factory descriptor after loading. Load contexts isolate dependencies but do not
sandbox trusted code. See [plugin-host.md](plugin-host.md).

## Typed driver observations

Drivers publish a closed family of immutable `RadioDriverObservation` records rather
than a discriminator accompanied by unrelated nullable payload fields. Frequency,
mode, VFO, split, transmit, complete-state, control, delivery-gap, ignored-frame, and
unknown-frame observations each carry only the data valid for that event.
Receiver-specific frequency, mode, selected-VFO, receive-path, and transmit-path
observations allow multi-receiver drivers to update only the addressed state and
freshness component without manufacturing a full-state report.

`ManagedRadio` processes these variants through type patterns and updates freshness
only for the state components described by the concrete observation. Driver authors
must preserve an unsupported or malformed native frame as an
`UnknownFrameObservation`; they must not manufacture a partially populated state
event. The `Kind` property remains available as a stable diagnostic discriminator.

`RadioState` represents receive/active VFO and transmit VFO separately. Drivers that
can select the split transmit VFO implement the explicit
`SetSplitAsync(enabled, transmitVfo)` operation. The original two-argument convenience
operation remains available, but adapters must report the driver's `TransmitVfo`
rather than inferring that transmission always uses the opposite receive VFO.

Physical hosts use a reconnectable managed-radio lifecycle. When the active protocol
stream fails, the runtime publishes `Faulted` and `Reconnecting` state, retries with
bounded exponential backoff, and asks the configured connector for an entirely new
transport, protocol session, and driver. After radio identification and a full state
read succeed, the replacement is swapped in through the command scheduler and a
`Connected` event is published. A faulted protocol object is never reused.

Hardware operations requested while reconnecting fail explicitly with
`RadioConnectionUnavailableException`; cached snapshots and connection events remain
available to every logical client.

Every queued hardware operation is also stamped with the connection generation that
was active when it was submitted. Fault, reconnecting, and successful replacement
transitions advance that generation. If a queued operation reaches the scheduler after
the connection has changed, it fails with `RadioOperationInvalidatedException` before
calling a driver. Setters, composite operations, and PTT operations are never silently
replayed against a replacement radio.

The same rule applies to hardware reads so callers never mistake a result from a
different connection for the operation they originally submitted. An application may
explicitly issue a new idempotent read after observing `Connected`; automatic retries
are intentionally not part of the runtime contract. Cached snapshots remain available
throughout recovery and are marked with their connection state.

Drivers classify an exception as `RadioConnectionException` only when the current
transport or protocol session is unsafe to reuse. The runtime feeds those command-path
failures into the same serialized recovery supervisor used by observation-stream
failures. Concurrent failure reports are deduplicated per driver, so they cannot start
competing reconnect loops. Invalid arguments, unsupported features, command rejection,
caller cancellation, and scheduler deadlines do not initiate recovery.

## State freshness and shared reads

State consumers choose their consistency requirement explicitly:

- `GetSnapshotAsync` and `RadioReadRequest.Cached` return the current in-memory state
  without CAT traffic.
- `RadioReadRequest.FreshWithin(age)` returns the cache while every state component is
  recent enough; otherwise all concurrent callers share one scheduled full-state read.
- `RefreshStateAsync` and `RadioReadRequest.ForceRefresh` always request a new hardware
  read.

Freshness is tracked for each VFO frequency and for active VFO, mode, split, and
transmit state. Recognized unsolicited CAT messages refresh only the components they
actually describe. A partial Yaesu `IF` announcement therefore cannot incorrectly
make the entire snapshot fresh. Fault and reconnect transitions invalidate freshness;
a successful reconnect establishes a new complete baseline. Cancelling one caller
does not cancel a shared refresh that other callers are awaiting.

## Event delivery and slow subscribers

Each event subscriber has an independent bounded queue of 256 events. A slow or
abandoned consumer therefore cannot cause unbounded process memory growth or delay
radio command execution. When its queue is full, the oldest queued event is discarded
so the consumer can eventually catch up to the newest radio state.

Loss is explicit rather than silent. Before the next retained event, that subscriber
receives a `Diagnostic` event containing `RadioEventDeliveryGap`, including the number
and sequence range of discarded events and the queue capacity. This diagnostic is
local to the affected subscription, which prevents one slow client from generating
extra traffic for every other client. Consumers that receive a gap should obtain a
fresh snapshot instead of trying to reconstruct current state solely from deltas.

The initial scope is transceivers. Plugin and hosting boundaries must not prevent future equipment categories, but no speculative equipment hierarchy will be introduced.
