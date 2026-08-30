# Concurrency and leases

Each physical radio has one asynchronous command scheduler. It owns framing, timing, response correlation, retries, cancellation boundaries, and unsolicited message dispatch.

Multiple logical clients may observe a radio concurrently. Mutations are authorized by role and policy. Transmit requires an explicit, renewable lease. Lease expiry, owning-client disconnect, or transport failure initiates a safety-priority PTT release.

An exclusive operation scope prevents commands from other clients being interleaved. It does not promise database-style rollback: radios generally cannot provide it. Multi-command failures report completed, failed, and unattempted operations, with optional compensating actions when a driver declares them safe.

Queue priorities must include fairness. Safety operations outrank normal traffic and cannot wait behind an unbounded queue.
