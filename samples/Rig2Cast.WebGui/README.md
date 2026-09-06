# Rig2Cast Web GUI POC

This independent sample hosts multiple Rig2Cast radios behind an ASP.NET Core REST/WebSocket boundary and serves a capability-driven browser UI from the same process. The UI contains no radio-model-specific control lists: model connection fields come from `RadioModelDescriptor`, while operational controls come from the connected driver's `RadioCapabilities`.

The server owns physical connections. A canonical serial port or raw TCP endpoint can be opened only once. The first browser opens and owns the radio; another browser requesting the same endpoint is attached to that existing `ManagedRadio` with a separate read-only observer session. Different endpoints create independent managed radios and may operate simultaneously.

## Run

```powershell
dotnet run --project samples/Rig2Cast.WebGui
```

Open `http://127.0.0.1:8080`. To listen on a VPN/LAN interface, explicitly set the ASP.NET Core URL, for example:

```powershell
dotnet run --project samples/Rig2Cast.WebGui -- --urls http://0.0.0.0:8080
```

The server is read-only by default. Non-PTT writes require both server permission and the connection-page checkbox:

```powershell
$env:Rig2Cast__AllowWrites='true'
dotnet run --project samples/Rig2Cast.WebGui
```

Remote PTT and arbitrary raw CAT are deliberately not exposed. This POC uses plain HTTP and has no authentication; expose it only on a trusted host/network or through a VPN. TLS and authentication belong before Internet exposure.

## API outline

- `GET /api/v1/models` and `/serial-ports`: capability-independent discovery.
- `GET /api/v1/radios`: active server-owned radio connections.
- `POST /api/v1/radios/connect`: open a new endpoint or attach to the existing radio using it.
- `POST /api/v1/radios/{radioId}/detach`: remove only the calling browser's observer session.
- `DELETE /api/v1/radios/{radioId}`: explicitly close a physical radio; only its opening browser may do this.
- `GET /api/v1/radios/{radioId}/snapshot` and `POST /refresh`: capability/state projection.
- Generic frequency, mode, split, numeric, switch, choice and meter routes are scoped by `radioId` and keyed by advertised identifiers.
- WebSocket `/api/v1/radios/{radioId}/events?clientId=...` sends an initial snapshot and updated snapshots for that radio.

REST operations on an attached radio require the browser's `X-Rig2Cast-Client` identity header. This identity separates sessions but is not authentication; the POC must remain on a trusted network or VPN.

Only the FTDX10 simulator is included in this first POC. All built-in models can connect over their advertised physical transports.
