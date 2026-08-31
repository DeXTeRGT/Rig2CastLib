# ADR 0003: One owner per physical connection

Status: Accepted

A managed radio session is the sole logical owner of a physical connection. The transport's
ownership transfers to the driver factory when `OpenAsync` is called. A successfully opened
driver owns and disposes that transport. If opening fails, the factory or driver must dispose it
before propagating the failure.

All logical clients use the managed radio's scheduler and never access the transport directly.
Exclusive operation scopes prevent interleaving but do not claim hardware rollback semantics.
