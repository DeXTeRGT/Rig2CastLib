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
or unknown manifest fields, and checks the loaded factory descriptor against every
manifest field. Driver API compatibility is currently an exact canonical
`major.minor` match, defined by `RadioDriverApiCompatibility`. Host `1.0` accepts only
plugin API `1.0`; it does not accept older/newer minor or major versions, and `1.0.0`
does not imply compatibility. Both the manifest and factory descriptor must declare
the same API version. Discovery rejects duplicate plugin and model IDs while
isolating per-manifest failures in diagnostics.

This conservative rule is the public compatibility guarantee for the initial SDK:
no forward or backward binary compatibility is promised. An additive minor-version
policy may be introduced only with contract tests covering old plugins on new hosts;
until then, changing the API version requires rebuilding the plugin for that exact
host API. Plugin package versions are independent and may use ordinary
`System.Version` components, but must exactly match between manifest and factory.

Each plugin uses a collectible assembly load context with its own dependency
resolver. `Rig2Cast.Abstractions` is shared with the host so factory interfaces retain
type identity. Disposal requests unload, but .NET unload is cooperative and the DLL
can remain mapped until all plugin objects are unreachable and garbage collection
runs. A load context is dependency isolation, not a security sandbox: trusted plugin
code has the permissions of the host process.

`RadioPluginCatalogComposition` discovers across configured directories, rejects
conflicts with built-in or previously loaded driver/model IDs, registers successful
factories in the existing `RadioDriverCatalog`, and owns their load contexts. Dispose
it only after every driver created by those factories has been disposed.

Catalog registration is an immutable startup snapshot: the first driver/model ID
wins. Hot replacement and side-by-side versions are deliberately unsupported. A
later conflicting load is isolated, reports `Duplicate`, and leaves the existing
registration operational. To update a plugin, stop its host, replace the plugin as a
single deployment unit, and restart with exactly one version present.

Registered plugin factories are lifetime-aware. Disposing their composition rejects
new opens and disposes the supplied transport, while already-open drivers remain
valid. The load context is asked to unload only after every active driver has disposed
its owned transport. This relies on the driver transport-ownership contract: a driver
must dispose its transport after failed open and at the end of driver disposal.

The diagnostic Console accepts `--plugin-config <file>`, repeatable
`--plugin-directory <path>` overrides, and the deliberately explicit
`--plugin-development-mode` trust bypass. Its strict JSON configuration is:

```json
{
  "pluginDirectories": ["plugins"],
  "trustRecords": [
    {
      "pluginId": "example.driver",
      "assemblySha256": "64_HEXADECIMAL_CHARACTERS"
    }
  ],
  "developmentMode": false
}
```

Relative directories in the file resolve against the configuration file's directory;
CLI directories resolve against the process working directory. Obtain the exact DLL
hash with `Get-FileHash .\Plugin.dll -Algorithm SHA256`. Duplicate directories or
trust identities and malformed hashes reject the configuration. Missing directories
are reported as isolated diagnostics. Development mode prints a warning and should
be used only for locally built, trusted code.

The catalog and managed runtime remain responsible for model selection, opening
transports, and validating instance capabilities. The plugin layer does not reference
adapters, servers, user interfaces, or protocol families. The Console retains its
specialized FTDX10 simulator. For any other model that advertises the `Simulator`
transport, `--simulator` opens its registered factory over an `InMemoryRadioTransport`.
Such a driver must provide its own virtual behavior; the in-memory transport does not
generate protocol responses.
