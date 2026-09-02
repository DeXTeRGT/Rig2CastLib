# Concurrency and leases

Each physical radio has one asynchronous command scheduler. It owns framing, timing, response correlation, retries, cancellation boundaries, and unsolicited message dispatch.

Multiple logical clients may observe a radio concurrently. Mutations are authorized by role and policy. Transmit requires an explicit, renewable lease. Lease expiry, owning-client disconnect, or transport failure initiates a safety-priority PTT release.

Lease-expiry de-keying uses bounded retries and reports every failed attempt as a
diagnostic without terminating the lease monitor. A replacement connection that
reports TX is forced to RX before it is published as connected when no valid
transmit lease exists. Shutdown treats de-keying as best effort but always proceeds
to scheduler, driver, and transport cleanup; cleanup failures are preserved for the
caller rather than preventing later resources from being closed.

An exclusive operation scope prevents commands from other clients being interleaved. It does not promise database-style rollback: radios generally cannot provide it. Multi-command failures report completed, failed, and unattempted operations, with optional compensating actions when a driver declares them safe.

Concurrent managed-runtime disposal callers share one completion and observe the
same cleanup result. Shutdown cancels active and queued scheduler work, continues
through driver and transport cleanup after individual failures, and disposes the
driver before awaiting an observation stream that may ignore cancellation.

Queue priorities must include fairness. Safety operations outrank normal traffic and cannot wait behind an unbounded queue.
