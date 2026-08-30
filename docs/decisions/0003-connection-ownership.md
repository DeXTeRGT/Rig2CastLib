# ADR 0003: One owner per physical connection

Status: Accepted

A managed radio session is the sole owner of a physical transport. All logical clients use its scheduler. Exclusive operation scopes prevent interleaving but do not claim hardware rollback semantics.
