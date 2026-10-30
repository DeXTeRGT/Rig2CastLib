import socket
import threading
import time

HOST = "0.0.0.0"
PORT = 6110

# =========================
# Choose rotor type here:
#   360.0  -> end-stop rotor 0..360
#   450.0  -> overlap rotor 0..450 (Yaesu-style)
# =========================
ROTOR_MAX_DEG = 450.0   # <--- change to 360.0 if needed

# Rotor simulation parameters
ROTATION_SPEED_DEG_PER_SEC = 3.6  # ~3.6°/s (450° in 125s-ish)
TICK_SECONDS = 0.5                # simulation tick interval


def clamp(v, lo, hi):
    return max(lo, min(hi, v))


class RotorState:
    def __init__(self, max_deg: float):
        self.max_deg = float(max_deg)
        self.angle = 0.0          # current azimuth, degrees in [0..max_deg]
        self.target = None        # target azimuth for Mxxx moves, or None
        self.direction = 0        # -1 = CCW, +1 = CW, 0 = stopped
        self.lock = threading.Lock()

    def set_manual(self, direction):
        """Start manual rotation (L/R), no fixed target."""
        with self.lock:
            self.direction = int(direction)
            self.target = None

    def stop(self):
        with self.lock:
            self.direction = 0
            self.target = None

    def set_move_to(self, target_deg):
        """Move linearly to target_deg within [0..max_deg]."""
        with self.lock:
            # Accept targets in the physical range only
            t = float(target_deg)
            t = clamp(t, 0.0, self.max_deg)

            self.target = t

            if t > self.angle:
                self.direction = +1
            elif t < self.angle:
                self.direction = -1
            else:
                self.direction = 0
                self.target = None

    def get_angle(self):
        with self.lock:
            return self.angle

    def step(self, dt):
        """Advance simulation by dt seconds, respecting end-stops."""
        with self.lock:
            if self.direction == 0:
                return

            step_deg = ROTATION_SPEED_DEG_PER_SEC * dt * self.direction
            new_angle = self.angle + step_deg

            # --- end-stops clamp ---
            if new_angle <= 0.0:
                new_angle = 0.0
                self.direction = 0
                self.target = None
                self.angle = new_angle
                return

            if new_angle >= self.max_deg:
                new_angle = self.max_deg
                self.direction = 0
                self.target = None
                self.angle = new_angle
                return

            # --- move-to target handling ---
            if self.target is not None:
                # If we crossed the target, clamp to target and stop
                if self.direction > 0 and new_angle >= self.target:
                    new_angle = float(self.target)
                    self.direction = 0
                    self.target = None
                elif self.direction < 0 and new_angle <= self.target:
                    new_angle = float(self.target)
                    self.direction = 0
                    self.target = None

            self.angle = new_angle


def simulation_loop(state: RotorState):
    """Background thread: updates rotor angle every TICK_SECONDS."""
    while True:
        time.sleep(TICK_SECONDS)
        state.step(TICK_SECONDS)


def handle_client(conn: socket.socket, addr, state: RotorState):
    print(f"[CLIENT] Connected from {addr}")
    with conn:
        buffer = b""
        while True:
            data = conn.recv(1024)
            if not data:
                print(f"[CLIENT] Disconnected from {addr}")
                break

            buffer += data

            # Commands are terminated by \r or \n
            while b"\n" in buffer or b"\r" in buffer:
                idx_r = buffer.find(b"\r")
                idx_n = buffer.find(b"\n")
                candidates = [i for i in (idx_r, idx_n) if i != -1]
                if not candidates:
                    break
                idx = min(candidates)

                line = buffer[:idx].decode("ascii", errors="ignore").strip()
                buffer = buffer[idx + 1:]

                if not line:
                    continue

                print(f"[RX] {line!r} from {addr}")
                response = handle_command(line, state)
                if response is not None:
                    try:
                        conn.sendall(response.encode("ascii"))
                        if response:
                            print(f"[TX] {response!r} to {addr}")
                    except OSError:
                        print("[CLIENT] Send failed, closing")
                        return


def handle_command(cmd: str, state: RotorState) -> str | None:
    """
    Process a single command line and return a reply string
    (including terminators), or None for no reply.
    """
    raw = cmd.strip()
    up = raw.upper()

    if up == "L":
        state.set_manual(direction=-1)
        return ""  # no reply

    if up == "R":
        state.set_manual(direction=+1)
        return ""  # no reply

    if up == "S":
        state.stop()
        return ""  # no reply

    if up == "C":
        # Read current Azimuth Angle
        angle = int(round(state.get_angle()))
        angle = int(clamp(angle, 0, int(state.max_deg)))

        # 3 digits covers 0..450 (e.g. 420), 360 too (e.g. 005)
        return f"{angle:03d}\r\n"

    if up.startswith("M") and len(up) >= 2:
        arg = raw[1:].strip()  # keep original formatting
        try:
            # allow "240.0" style too
            target = float(arg)
        except ValueError:
            return ""

        state.set_move_to(target)
        return ""  # no immediate reply

    if up == "Q":
        state.stop()
        return "BYE\r\n"

    return ""


def main():
    state = RotorState(ROTOR_MAX_DEG)

    sim_thread = threading.Thread(target=simulation_loop, args=(state,), daemon=True)
    sim_thread.start()

    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        s.bind((HOST, PORT))
        s.listen(1)
        print(f"[SERVER] Yaesu mock (end-stops 0..{int(ROTOR_MAX_DEG)}) listening on {HOST}:{PORT}")

        while True:
            conn, addr = s.accept()
            client_thread = threading.Thread(target=handle_client, args=(conn, addr, state), daemon=True)
            client_thread.start()


if __name__ == "__main__":
    main()
