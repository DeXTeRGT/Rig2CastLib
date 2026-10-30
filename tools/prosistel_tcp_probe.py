#!/usr/bin/env python3
"""Passive-first diagnostic console for a Prosistel controller on raw TCP.

The program sends nothing automatically. Use the interactive commands after
observing the connection, or pass --query for one explicit position query.
"""

from __future__ import annotations

import argparse
import datetime as dt
import re
import socket
import threading
import time


STX = 0x02
CR = 0x0D
POSITION_REPLY = re.compile(rb"^\x02([A-Za-z]),\?,([+-]?\d+(?:\.\d+)?),([A-Za-z])\r$")


def timestamp() -> str:
    return dt.datetime.now().astimezone().isoformat(timespec="milliseconds")


def printable(data: bytes) -> str:
    parts: list[str] = []
    for value in data:
        if value == STX:
            parts.append("<STX>")
        elif value == CR:
            parts.append("<CR>")
        elif 32 <= value <= 126:
            parts.append(chr(value))
        else:
            parts.append(f"<0x{value:02X}>")
    return "".join(parts)


class Probe:
    def __init__(self, host: str, port: int, timeout: float) -> None:
        self._host = host
        self._port = port
        self._socket = socket.create_connection((host, port), timeout=timeout)
        self._socket.settimeout(0.5)
        self._stop = threading.Event()
        self._print_lock = threading.Lock()
        self._reader = threading.Thread(target=self._receive_loop, name="prosistel-rx", daemon=True)

    def start(self) -> None:
        self.log("INFO", f"connected to {self._host}:{self._port}; passive receive mode")
        self._reader.start()

    def close(self) -> None:
        self._stop.set()
        try:
            self._socket.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        self._socket.close()
        self._reader.join(timeout=1)

    def log(self, direction: str, message: str) -> None:
        with self._print_lock:
            print(f"[{timestamp()}] {direction:<5} {message}", flush=True)

    def send(self, payload: bytes) -> None:
        self._socket.sendall(payload)
        self.log("TX", f"hex={payload.hex(' ')}  text={printable(payload)}")

    def _receive_loop(self) -> None:
        buffered = bytearray()
        while not self._stop.is_set():
            try:
                chunk = self._socket.recv(4096)
                if not chunk:
                    self.log("INFO", "remote endpoint closed the connection")
                    self._stop.set()
                    return
                self.log("RX", f"chunk hex={chunk.hex(' ')}  text={printable(chunk)}")
                buffered.extend(chunk)
                while CR in buffered:
                    end = buffered.index(CR) + 1
                    frame = bytes(buffered[:end])
                    del buffered[:end]
                    self._show_frame(frame)
            except socket.timeout:
                continue
            except OSError as error:
                if not self._stop.is_set():
                    self.log("ERROR", f"receive failed: {error}")
                self._stop.set()
                return
        if buffered:
            self.log("FRAME", f"unterminated hex={buffered.hex(' ')}  text={printable(buffered)}")

    def _show_frame(self, frame: bytes) -> None:
        self.log("FRAME", f"hex={frame.hex(' ')}  text={printable(frame)}")
        match = POSITION_REPLY.match(frame)
        if match:
            axis, position, result = (part.decode("ascii") for part in match.groups())
            self.log("PARSE", f"axis={axis.upper()} position={position} result={result.upper()}")


HELP = """Commands (nothing is sent until you enter one):
  query [A]             Query the position: <STX>A?<CR>
  cpm-off [A]           Disable continuous position monitoring: <STX>AS<CR>
  goto DEGREES [A]      Confirm, then send <STX>AG<degrees><CR>
  stop [A]              Confirm, then send the verified stop target 977
  raw HEX               Send explicit hex bytes, e.g. raw 02 41 3f 0d
  help                  Show this help
  quit                  Close the TCP connection

This controller/firmware was verified to use G977 as its motion-stop command.
"""


def axis_value(value: str) -> str:
    axis = value.upper()
    if len(axis) != 1 or not axis.isascii() or not axis.isalpha():
        raise ValueError("axis must be one ASCII letter, normally A")
    return axis


def frame(axis: str, operation: str) -> bytes:
    return bytes([STX]) + axis.encode("ascii") + operation.encode("ascii") + bytes([CR])


def confirm(prompt: str) -> bool:
    return input(f"{prompt} Type YES to continue: ").strip() == "YES"


def interactive(probe: Probe, default_axis: str) -> None:
    print(HELP)
    while True:
        try:
            words = input("prosistel> ").strip().split()
        except (EOFError, KeyboardInterrupt):
            print()
            return
        if not words:
            continue
        command = words[0].lower()
        try:
            if command in {"quit", "exit"}:
                return
            if command == "help":
                print(HELP)
            elif command in {"query", "cpm-off"}:
                axis = axis_value(words[1] if len(words) > 1 else default_axis)
                probe.send(frame(axis, "?" if command == "query" else "S"))
            elif command == "goto":
                if len(words) not in {2, 3}:
                    raise ValueError("usage: goto DEGREES [AXIS]")
                degrees = float(words[1])
                if not 0 <= degrees <= 360:
                    raise ValueError("goto heading must be between 0 and 360 degrees")
                axis = axis_value(words[2] if len(words) == 3 else default_axis)
                encoded = f"{degrees:g}"
                if confirm(f"This can move the antenna to {encoded} degrees."):
                    probe.send(frame(axis, f"G{encoded}"))
            elif command == "stop":
                if len(words) not in {1, 2}:
                    raise ValueError("usage: stop [AXIS]")
                axis = axis_value(words[1] if len(words) == 2 else default_axis)
                if confirm("Send verified Prosistel stop command G977?"):
                    probe.send(frame(axis, "G977"))
            elif command == "raw":
                if len(words) < 2:
                    raise ValueError("usage: raw HEX [HEX ...]")
                payload = bytes.fromhex(" ".join(words[1:]))
                if confirm(f"Send {len(payload)} unvalidated raw bytes?"):
                    probe.send(payload)
            else:
                print("Unknown command. Enter 'help' for available commands.")
        except (ValueError, OSError) as error:
            probe.log("ERROR", str(error))


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Inspect a Prosistel controller through a raw TCP serial endpoint.")
    parser.add_argument("host", help="raw TCP serial endpoint host or IP address")
    parser.add_argument("port", type=int, help="raw TCP serial endpoint port")
    parser.add_argument("--axis", default="A", type=axis_value, help="default protocol axis (default: A)")
    parser.add_argument("--connect-timeout", type=float, default=5.0, help="TCP connection timeout in seconds")
    parser.add_argument("--query", action="store_true", help="send one position query after connecting")
    parser.add_argument("--listen-seconds", type=float, help="listen for this duration instead of opening the console")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    probe: Probe | None = None
    try:
        probe = Probe(args.host, args.port, args.connect_timeout)
        probe.start()
        if args.query:
            probe.send(frame(args.axis, "?"))
        if args.listen_seconds is not None:
            time.sleep(max(0, args.listen_seconds))
        else:
            interactive(probe, args.axis)
        return 0
    except (ConnectionError, OSError) as error:
        print(f"Connection error: {error}")
        return 1
    finally:
        if probe is not None:
            probe.close()


if __name__ == "__main__":
    raise SystemExit(main())
