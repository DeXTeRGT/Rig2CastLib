# ADR 0005: Lease-protected transmit

Status: Accepted

PTT requires a renewable transmit lease. Loss of the lease or client connection triggers safety-priority PTT release. Adapters such as rigctld must obey this invariant, potentially through policy-controlled implicit leases.
