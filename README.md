# Rig2Cast

Rig2Cast is a modular .NET 8 transceiver-control platform. It is designed as an embeddable library first, with optional service and protocol adapters built on the same runtime.

The current milestone establishes architectural contracts and a simulator-led vertical slice. The Yaesu FTDX10 is the first physical transceiver target.

Hamlib in the sibling directory is reference material only. Rig2Cast is not a C# port of Hamlib. See `docs/protocol-provenance.md`.

## Status

Early vertical-slice development. The runtime currently includes serialized radio access, logical client sessions, roles, transmit leases, exclusive operation scopes, versioned events, typed numeric/switch/choice controls and raw meters, an FTDX10-shaped simulator, and serial/in-memory transports. Public APIs are not stable.

## Build and test

```powershell
dotnet build Rig2Cast.sln
dotnet test tests\Rig2Cast.Runtime.Tests\Rig2Cast.Runtime.Tests.csproj
dotnet run --project samples\Rig2Cast.Demo\Rig2Cast.Demo.csproj
```

The physical-radio smoke utility performs identification, state, and raw meter queries only:

```powershell
dotnet run --project samples\Rig2Cast.Ftdx10Smoke\Rig2Cast.Ftdx10Smoke.csproj -- --port COM11 --baud 38400
```
