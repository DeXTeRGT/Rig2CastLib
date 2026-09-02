# Plugin host

`Rig2Cast.PluginHost` discovers external radio-driver assemblies from sidecar files
named `*.rig2cast-plugin.json`. The host reads and validates the manifest before it
loads executable code. Each manifest declares the driver identity and API version,
entry DLL, factory type, and complete static model metadata: stable ID, display
names, transports, baud rates, default baud, and connection defaults.

Production loading requires exactly one matching `PluginTrustRecord`. Its plugin ID
and SHA-256 assembly hash must match the manifest and DLL. `DevelopmentMode` is an
explicit trust bypass intended only for local development. It must not be enabled
for directories writable by untrusted users.

The loader accepts only a DLL file name in the manifest directory, rejects malformed
or unknown manifest fields, requires the exact configured driver API version, and
checks the loaded factory descriptor against every manifest field. Discovery rejects
duplicate plugin and model IDs while isolating per-manifest failures in diagnostics.

Each plugin uses a collectible assembly load context with its own dependency
resolver. `Rig2Cast.Abstractions` is shared with the host so factory interfaces retain
type identity. Disposal requests unload, but .NET unload is cooperative and the DLL
can remain mapped until all plugin objects are unreachable and garbage collection
runs. A load context is dependency isolation, not a security sandbox: trusted plugin
code has the permissions of the host process.

Discovery returns loaded factories; a composition host may register them in the
existing `RadioDriverCatalog`. The catalog and the managed runtime remain responsible
for model selection, opening transports, and validating instance capabilities. The
plugin layer does not reference adapters, servers, user interfaces, or protocol
families.
