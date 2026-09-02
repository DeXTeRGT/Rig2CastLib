# Declarative external driver example

This independent plugin demonstrates the frozen version-1 compiled C# descriptor
vocabulary. It references only `Rig2Cast.Abstractions` and `Rig2Cast.Protocols`; it
does not reference the runtime, plugin host, Console, adapters, or server projects.

The virtual read-only radio demonstrates:

- `ValueMapDescriptor` for mode wire codes;
- `NumericFieldDescriptor` and `AsciiQueryDescriptor` for `SM127;`;
- `ModeApplicabilityDescriptor` for USB/CW/FM tuning-step choices;
- `ConditionalValueSetDescriptor` for an optional second preamp;
- capabilities generated from the same declarations;
- driver ownership of the host-supplied transport.

Build it separately from the main solution:

```powershell
dotnet build .\samples\Rig2Cast.DeclarativeExamplePlugin\Rig2Cast.DeclarativeExamplePlugin.csproj
```

Run it through the real Console/plugin-host composition:

```powershell
dotnet run --project .\samples\Rig2Cast.Console\Rig2Cast.Console.csproj -- `
  --model rig2cast.example.declarative-radio --simulator `
  --plugin-directory .\samples\Rig2Cast.DeclarativeExamplePlugin\bin\Debug\net8.0 `
  --plugin-development-mode
```

Try these commands:

```text
state
capabilities core
capabilities choices
capabilities meters
get choice TuningStep
get choice Preamp
meters
quit
```

Expected highlights are USB at 14.2 MHz, tuning step `10hz`, preamp `off`, and raw
signal strength 127 (about 49.8%). The meter response is a deterministic in-driver
fixture so this sample remains usable with `InMemoryRadioTransport`; it is not a full
ASCII protocol simulator.

`secondPreamp` is a model connection setting used to demonstrate a typed conditional
hook. Its default is `false`. A production host may override it when constructing
`RadioConnectionOptions`; the diagnostic Console currently uses model defaults for
physical connections and an empty setting set for generic simulator plugins.

This sample shows the recommended division of responsibility, not a no-code driver:
descriptors own regular data and validation, while a real driver still owns command
sequencing, vendor errors, option discovery, and protocol-specific behavior. Framing
and response correlation belong in a protocol-family session.
