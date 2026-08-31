# rigctld compatibility adapter

`Rig2Cast.Adapters.Rigctld` is an optional adapter project. It depends only on
`Rig2Cast.Abstractions`; it does not make the Rig2Cast runtime depend on Hamlib.
`Rig2Cast.RigctldHost` composes the adapter with one managed FTDX10.

## Safety and concurrency

- Listens on `127.0.0.1:4532` and is read-only by default.
- Every TCP connection has a distinct Rig2Cast identity and session.
- Each client's commands run in order; the existing radio scheduler serializes
  commands from all clients onto the one CAT connection.
- State getters accept a snapshot up to 250 ms old. When it expires, simultaneous
  clients share one full-state CAT refresh instead of multiplying radio traffic.
- Commands invalidated by a disconnect are returned as a rigctld I/O error and are
  never replayed automatically after reconnection. Clients may retry a read after the
  radio reports connected; setters require a new explicit client command.
- The default limit is 32 clients and commands are limited to 4096 characters.
- Disconnecting disposes the session and releases its resources.
- PTT setters remain unavailable even when ordinary writes are enabled.
- `--listen-any` exposes an unauthenticated protocol. Do not use it on an
  untrusted network.

## Current coverage

Short and long commands (`f` / `\\get_freq`) are accepted. Extended responses
using `+` or another separator are supported.

| Command | Status |
| --- | --- |
| `f`, `F` | Get/set frequency of the active VFO |
| `v`, `V` | Get/set active VFO (`VFOA`, `VFOB`) |
| `m`, `M` | Get mode and native filter width; set mode and the closest supported mode-valid width atomically |
| `s`, `S` | Get/set split; the FTDX10 uses the opposite VFO as TX VFO |
| `t` | Get PTT state |
| `T` | Intentionally unavailable (`RPRT -11`) |
| `q` | Close the connection |

Unknown commands return `RPRT -4`; disabled writes return `RPRT -9`.

## Test with the simulator

List the models registered by the host without opening a radio:

```powershell
dotnet run --project samples\Rig2Cast.RigctldHost -- --list-models
```

```powershell
dotnet run --project samples\Rig2Cast.RigctldHost -- --model yaesu.ftdx10 --simulator
```

In another PowerShell window:

```powershell
$client = [Net.Sockets.TcpClient]::new('127.0.0.1', 4532)
$stream = $client.GetStream()
$writer = [IO.StreamWriter]::new($stream, [Text.Encoding]::ASCII, 1024, $true)
$reader = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII, $false, 1024, $true)
$writer.NewLine = "`n"
$writer.WriteLine('f'); $writer.Flush(); $reader.ReadLine()
$writer.WriteLine('+m'); $writer.Flush(); 1..4 | ForEach-Object { $reader.ReadLine() }
$client.Dispose()
```

The initial simulator frequency is `14200000`. A filter set to `2400hz` is
reported to rigctld clients as passband `2400`; the native `default` setting is
reported as `0`.

## Test with the FTDX10

Ensure no other program has COM11 open, then start read-only:

```powershell
dotnet run --project samples\Rig2Cast.RigctldHost -- --model yaesu.ftdx10 --serial-port COM11 --baud 38400
```

For the FTDX10 enhanced USB CAT port, add `--auto-information` to keep the shared
state cache synchronized from supported front-panel announcements. The Yaesu manual
documents `AI` as USB-only, so do not enable this option for an RS-232 connection.

Enable non-PTT setters explicitly with `--allow-write`. Write-enabled connections
receive the Controller role so composite operations such as mode plus passband can
hold an exclusive-control lease. Other options are
`--tcp-port`, `--max-clients`, and `--listen-any`.
