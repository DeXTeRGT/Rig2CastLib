# ADR 0004: Manifest-discovered trusted plugins

Status: Accepted

Sidecar manifests allow discovery without loading assemblies. Plugins are separate assemblies, are not distributed through NuGet, and require explicit trust by identity and binary hash. A development override is allowed.
