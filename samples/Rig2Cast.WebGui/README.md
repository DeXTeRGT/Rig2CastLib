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

## Install as a Linux service under `/opt`

The deployment below is self-contained, so the target does not need a separate
.NET runtime. It must be published for the target Linux architecture. Use
`linux-x64` on an Intel/AMD host or `linux-arm64` on a 64-bit ARM host such as a
recent Raspberry Pi.

Publish on the Linux target (or publish elsewhere and copy the resulting directory):

```bash
dotnet publish samples/Rig2Cast.WebGui/Rig2Cast.WebGui.csproj \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained true \
  --output /tmp/rig2cast-webgui-publish
```

Then install it. Run these commands from the repository root:

```bash
sudo useradd --system --user-group --home-dir /nonexistent \
  --shell /usr/sbin/nologin rig2cast
sudo usermod --append --groups dialout rig2cast

sudo install -d -o root -g root -m 0755 /opt/rig2cast-webgui
sudo cp -a /tmp/rig2cast-webgui-publish/. /opt/rig2cast-webgui/
sudo chown -R root:root /opt/rig2cast-webgui
sudo chmod -R a-w /opt/rig2cast-webgui
sudo chmod 0755 /opt/rig2cast-webgui/Rig2Cast.WebGui

sudo install -o root -g root -m 0644 \
  samples/Rig2Cast.WebGui/deploy/linux/rig2cast-webgui.service \
  /etc/systemd/system/rig2cast-webgui.service
sudo install -o root -g root -m 0640 \
  samples/Rig2Cast.WebGui/deploy/linux/rig2cast-webgui.env.example \
  /etc/rig2cast-webgui.env

sudo systemctl daemon-reload
sudo systemctl enable --now rig2cast-webgui
```

Edit `/etc/rig2cast-webgui.env` before exposing the service. The default listens
only on `127.0.0.1:8080`. Set `URLS=http://0.0.0.0:8080` for access on a trusted
LAN/VPN, and set `Rig2Cast__AllowWrites=true` only when radio control and PTT are
intended. This sample has no authentication or TLS and should not be exposed
directly to the Internet.

Useful service commands:

```bash
systemctl status rig2cast-webgui
journalctl -u rig2cast-webgui -f
sudo systemctl restart rig2cast-webgui
curl http://127.0.0.1:8080/api/v1/status
```

Serial radios normally appear as `/dev/ttyUSB*` or `/dev/ttyACM*`. The service user
is placed in the conventional `dialout` group for access. On distributions using a
different device-owning group, replace `dialout` in both the `usermod` command and
the unit's `SupplementaryGroups` line. If a device still cannot be opened, inspect
its ownership with `ls -l /dev/ttyUSB0` (using the actual device name) and restart
the service after correcting the group or an applicable udev rule.

To deploy an update, stop the service, replace the contents of
`/opt/rig2cast-webgui` with a new publish output, restore the ownership and
read-only permissions shown above, and start it again. Preserve
`/etc/rig2cast-webgui.env`; configuration intentionally lives outside `/opt`.

The server is read-only by default. Radio writes, including PTT, require both server permission and the connection-page checkbox:

```powershell
$env:Rig2Cast__AllowWrites='true'
dotnet run --project samples/Rig2Cast.WebGui
```

PTT is available only to the owning Operator page when the radio advertises writable transmit support. It uses a 10-second transmit lease that the controlling browser renews every five seconds; loss of the page or connection stops renewal and lets the runtime force RX. Arbitrary raw CAT is deliberately not exposed. This POC uses plain HTTP and has no authentication; expose it only on a trusted host/network or through a VPN. TLS and authentication belong before Internet exposure.

## API outline

- `GET /api/v1/models` and `/serial-ports`: capability-independent discovery.
- `GET /api/v1/radios`: active server-owned radio connections.
- `POST /api/v1/radios/connect`: open a new endpoint or attach to the existing radio using it.
- `POST /api/v1/radios/{radioId}/detach`: remove only the calling browser's observer session.
- `DELETE /api/v1/radios/{radioId}`: explicitly close a physical radio; only its opening browser may do this.
- `GET /api/v1/radios/{radioId}/snapshot` and `POST /refresh`: capability/state projection.
- Generic frequency, mode, split, numeric, switch, choice and meter routes are scoped by `radioId` and keyed by advertised identifiers.
- `PUT /api/v1/radios/{radioId}/ptt` toggles lease-protected PTT and `POST /ptt/renew` renews an active browser-owned transmit lease.
- WebSocket `/api/v1/radios/{radioId}/events?clientId=...` sends an initial snapshot and updated snapshots for that radio.

Writable VFO frequency displays support mouse-wheel tuning using the driver's smallest advertised step. Hold Shift for 10x or Ctrl for 100x. Split transmit VFO cards use a red border while the active receive VFO remains cyan.

These interactions were physically validated on an FTDX10 on 2026-09-06: wheel
tuning addressed the intended VFO with each modifier, the split transmit border
followed the TX VFO, PTT toggled normally, and loss of browser renewal returned the
radio to RX after the lease safety window.

Known deferred presentation gap: the passband field does not yet reliably retain its
read value across WebSocket redraws or derive its discrete/ranged limits from the
current mode's passband constraint. Driver/runtime passband support is present, and
the existing Filter Width and roofing-filter controls remain available until the
canonical passband presentation is completed.

REST operations on an attached radio require the browser's `X-Rig2Cast-Client` identity header. This identity separates sessions but is not authentication; the POC must remain on a trusted network or VPN.

Only the FTDX10 simulator is included in this first POC. All built-in models can connect over their advertised physical transports.

## Browser audio streaming

The Audio tab is a duplex client for a GhostLink/AUDIO_STREAMING_CS_LINUX server. The WebGui process bridges browser WebSocket PCM to the server's dual TCP streams and uses the same Concentus 2.2.2 Opus codec and wire format as that server. Configure the ports from the server's perspective: **TX port** carries radio audio to the browser and **RX port** receives browser microphone audio. Defaults are 6001 and 6002.

Audio is fixed at the server-compatible 48 kHz, mono, 20 ms format. Browser microphone access works on localhost or an HTTPS origin. This endpoint has no authentication and can open outbound TCP connections, so keep the WebGui on a trusted LAN/VPN and do not expose it directly to the Internet.
