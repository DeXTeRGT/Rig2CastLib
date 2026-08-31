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

Physical hosts use a reconnectable managed-radio lifecycle. When the active protocol
stream fails, the runtime publishes `Faulted` and `Reconnecting` state, retries with
bounded exponential backoff, and asks the configured connector for an entirely new
transport, protocol session, and driver. After radio identification and a full state
read succeed, the replacement is swapped in through the command scheduler and a
`Connected` event is published. A faulted protocol object is never reused.

Hardware operations requested while reconnecting fail explicitly with
`RadioConnectionUnavailableException`; cached snapshots and connection events remain
available to every logical client.

The initial scope is transceivers. Plugin and hosting boundaries must not prevent future equipment categories, but no speculative equipment hierarchy will be introduced.
