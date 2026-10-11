"""Exercise the real Linux application in a resizable pseudo-terminal."""
import fcntl
import os
from pathlib import Path
import pty
import select
import struct
import subprocess
import termios
import time

output = Path("terminal-smoke-output")
output.mkdir(exist_ok=True)
master, slave = pty.openpty()
fcntl.ioctl(master, termios.TIOCSWINSZ, struct.pack("HHHH", 24, 80, 0, 0))
process = subprocess.Popen(["dotnet", "cli/bin/Release/net8.0/pramaan.dll"], stdin=slave,
                           stdout=slave, stderr=slave, env={**os.environ, "TERM": "xterm-256color", "LANG": "C.UTF-8"})
os.close(slave)
all_output = bytearray()


def read_for(seconds):
    captured = bytearray()
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        ready, _, _ = select.select([master], [], [], 0.1)
        if ready:
            try:
                data = os.read(master, 65536)
            except OSError:
                break
            if not data:
                break
            captured.extend(data)
    all_output.extend(captured)
    return captured.decode(errors="replace")


try:
    initial = read_for(10)
    if "ACTIONS" not in initial or "PROGRESS" not in initial:
        raise RuntimeError("Application did not render essential panels in a real terminal")
    for width, height in ((100, 32), (140, 45), (160, 50), (60, 20), (80, 24)):
        fcntl.ioctl(master, termios.TIOCSWINSZ, struct.pack("HHHH", height, width, 0, 0))
        frame = read_for(1)
        (output / f"{width}x{height}.ansi").write_text(frame)
        if process.poll() is not None or "ACTIONS" not in frame:
            raise RuntimeError(f"Application failed to redraw on resize to {width}x{height}")
    os.write(master, b"\t")
    if "DETAILS" not in read_for(1):
        raise RuntimeError("Tab did not switch views")
    os.write(master, b"\x1b[6~")  # Page Down
    read_for(1)
    os.write(master, b"q")
    read_for(1)
    if process.wait(timeout=10) != 0:
        raise RuntimeError("Application did not exit cleanly")
    print("PASS: real Linux terminal resizing, view switching, paging, and clean exit")
finally:
    (output / "session.ansi").write_bytes(all_output)
    if process.poll() is None:
        process.terminate()
        process.wait(timeout=10)
    os.close(master)
