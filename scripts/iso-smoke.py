"""Boot the actual ISO under BIOS and UEFI; retain screenshots and OCR evidence.

Uses only disposable VM firmware state. Does not attach host disks or submit QC.
"""
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import time

iso = Path(sys.argv[1]).resolve()
output = Path("iso-smoke-output").resolve()
output.mkdir(exist_ok=True)


def boot(mode):
    with tempfile.TemporaryDirectory(prefix="pramaan-vm-") as scratch:
        endpoint = str(Path(scratch) / "qmp.sock")
        args = ["qemu-system-x86_64", "-m", "2048", "-smp", "2", "-display", "none",
                "-vga", "std", "-cdrom", str(iso), "-boot", "d", "-no-reboot",
                "-nic", "user,model=e1000", "-qmp", f"unix:{endpoint},server=on,wait=off"]
        if os.access("/dev/kvm", os.R_OK | os.W_OK):
            args += ["-accel", "kvm"]
        if mode == "uefi":
            firmware = Path("/usr/share/OVMF")
            code = firmware / "OVMF_CODE_4M.fd"
            template = firmware / "OVMF_VARS_4M.fd"
            if not code.exists():
                code, template = firmware / "OVMF_CODE.fd", firmware / "OVMF_VARS.fd"
            variables = Path(scratch) / "vars.fd"
            shutil.copyfile(template, variables)
            args += ["-drive", f"if=pflash,format=raw,readonly=on,file={code}",
                     "-drive", f"if=pflash,format=raw,file={variables}"]
        with (output / f"{mode}-qemu.log").open("w") as log:
            process = subprocess.Popen(args, stdout=log, stderr=subprocess.STDOUT)
            connection = socket.socket(socket.AF_UNIX)
            connection.settimeout(30)
            try:
                deadline = time.monotonic() + 480
                while not Path(endpoint).exists():
                    if process.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError(f"{mode}: QEMU failed to start")
                    time.sleep(1)
                connection.connect(endpoint)
                reader = connection.makefile("r")
                reader.readline()  # QMP greeting

                def command(name, arguments=None):
                    packet = {"execute": name}
                    if arguments is not None:
                        packet["arguments"] = arguments
                    connection.sendall((json.dumps(packet) + "\n").encode())
                    while True:
                        response = json.loads(reader.readline())
                        if "error" in response:
                            raise RuntimeError(response["error"])
                        if "return" in response:
                            return response["return"]

                command("qmp_capabilities")

                def capture(label):
                    picture = output / f"{mode}-{label}.ppm"
                    command("screendump", {"filename": str(picture)})
                    text = subprocess.check_output(["tesseract", str(picture), "stdout", "--psm", "6"], stderr=subprocess.DEVNULL, text=True)
                    (output / f"{mode}-{label}.txt").write_text(text)
                    return text.upper()

                while time.monotonic() < deadline:
                    if process.poll() is not None:
                        raise RuntimeError(f"{mode}: guest exited before dashboard")
                    text = capture("dashboard")
                    # VGA's dark magenta panel headings can be missed by OCR.
                    # Require independent application and content markers instead.
                    if "PRAMAAN" in text and "PROGRESS" in text and "FULL QC" in text:
                        break
                    time.sleep(5)
                else:
                    raise RuntimeError(f"{mode}: dashboard not found; inspect screenshot and OCR artifacts")
                command("human-monitor-command", {"command-line": "sendkey tab"})
                time.sleep(2)
                details = capture("details")
                if "DETAILS" not in details and "NO SCAN DATA" not in details:
                    raise RuntimeError(f"{mode}: Tab did not switch to Details")
                command("human-monitor-command", {"command-line": "sendkey q"})
                time.sleep(3)
                if "EXITED" not in capture("exit"):
                    raise RuntimeError(f"{mode}: Q did not return to the launcher")
                print(f"PASS: {mode} boots the ISO, renders dashboard, switches views, and exits", flush=True)
            finally:
                connection.close()
                process.terminate()
                try:
                    process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()


for boot_mode in ("bios", "uefi"):
    boot(boot_mode)
