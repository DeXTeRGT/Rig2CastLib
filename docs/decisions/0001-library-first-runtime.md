# ADR 0001: Library-first runtime

Status: Accepted

Rig2Cast implements radio control in framework-neutral libraries. The standalone server and protocol adapters consume the same public runtime contracts. This permits embedded and remote use without duplicating session or safety behavior.
