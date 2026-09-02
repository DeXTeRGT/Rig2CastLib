# External driver plugin example

This project is deliberately outside the main solution's built-in driver graph. It
references only `Rig2Cast.Abstractions` and demonstrates factory metadata, transport
ownership, a read-only capability document, state construction, and a sidecar
manifest. It is a virtual reference radio, not support for physical hardware.

Build it independently:

```powershell
dotnet build .\samples\Rig2Cast.ExamplePlugin\Rig2Cast.ExamplePlugin.csproj
```

For a quick local discovery check only:

```powershell
dotnet run --project .\samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- `
  --plugin-directory .\samples\Rig2Cast.ExamplePlugin\bin\Debug\net8.0 `
  --plugin-development-mode --list-models
```

Run the virtual reference radio through the Console:

```powershell
dotnet run --project .\samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- `
  --model rig2cast.example.reference-radio --simulator `
  --plugin-directory .\samples\Rig2Cast.ExamplePlugin\bin\Debug\net8.0 `
  --plugin-development-mode
```

The example is deliberately read-only, so `--allow-write` is unnecessary and its
declared capabilities cause mutation requests to be rejected by the runtime.

Production-style trust uses the exact assembly hash:

```powershell
Get-FileHash .\samples\Rig2Cast.ExamplePlugin\bin\Debug\net8.0\Rig2Cast.ExamplePlugin.dll -Algorithm SHA256
```

Copy that hash into a plugin-host configuration described in
[`docs/plugin-host.md`](../../docs/plugin-host.md), keep `developmentMode` false, and
pass the file with `--plugin-config`. Rebuilds change the hash and require an explicit
trust update.

Do not copy the example's static state behavior into a physical driver. A real driver
must put framing/correlation in a protocol-family layer, derive capabilities from the
selected model/firmware/options, perform verified hardware reads, classify terminal
connection failures, and preserve the transport ownership contract.
