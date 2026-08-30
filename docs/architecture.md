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

The initial scope is transceivers. Plugin and hosting boundaries must not prevent future equipment categories, but no speculative equipment hierarchy will be introduced.
