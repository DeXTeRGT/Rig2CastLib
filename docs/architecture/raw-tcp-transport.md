# Raw TCP CAT transport

`TcpRadioTransport` connects as a TCP client to a server that exposes a transparent
serial byte stream. Every CAT byte is carried unchanged. It does not implement
Telnet, RFC2217, rigctld, WebSocket, text encoding, or message framing.

```text
radio <-> serial port <-> raw TCP server <-> TcpRadioTransport <-> CAT protocol
```

The transport is configured with `TcpRadioTransportOptions`: host, port, connect
timeout, TCP no-delay, and keep-alive. `NoDelay` defaults to true to avoid unnecessary
latency for short CAT commands. Serial framing and control-line settings belong to
the remote bridge and are not sent over raw TCP.

All built-in radio models advertise `RadioTransportKind.Tcp` because their existing
CAT byte streams can use a transparent bridge. This is transport capability, not a
claim that the physical radio has native Ethernet support. Drivers continue to
receive only `IRadioTransport` and contain no TCP-specific logic.

Reconnectable hosts must create a fresh `TcpRadioTransport`/`TcpClient` for every
connection attempt. A zero-byte read means the remote endpoint closed the stream;
the protocol/runtime connection supervisor handles that failure normally. Disposal
closes the socket so blocked reads are released.

The initial implementation intentionally supports raw TCP only. RFC2217/Telnet
negotiation, TLS, authentication, and server/listener operation are separate concerns.
Raw CAT commonly has no authentication or encryption, so it should be restricted to
localhost or a trusted network/VPN, especially when write/PTT control is enabled.

Physical validation on 2026-09-04 used the Xiegu G90 through a VSPE raw TCP server at
`127.0.0.1:5555`. The Console connected successfully, read CI-V numeric controls, and
confirmed targeted VFO A frequency writes. This validates the complete path from
`TcpRadioTransport` through the existing CI-V session and G90 driver to the radio.
