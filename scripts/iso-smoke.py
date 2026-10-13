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
                "-vga", "std",
                "-nic", "user,model=e1000", "-qmp", f"unix:{endpoint},server=on,wait=off"]
        usb_image = os.environ.get("PRAMAAN_USB_IMAGE")
        if usb_image:
            args += ["-device", "usb-ehci,id=ehci", "-drive", f"if=none,id=stick,format=raw,file={usb_image}",
                     "-device", "usb-storage,bus=ehci.0,drive=stick,bootindex=1"]
        else:
            args += ["-cdrom", str(iso), "-boot", "d", "-no-reboot"]
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

                def wait_screen(label, predicate, timeout=30):
                    limit = time.monotonic() + timeout
                    while time.monotonic() < limit:
                        text = capture(label)
                        if predicate(text):
                            return text
                        time.sleep(1)
                    raise RuntimeError(f"{mode}: expected screen {label} not found; inspect screenshot and OCR artifacts")

                def wait_dashboard(label, fresh=False):
                    limit = time.monotonic() + 480
                    while time.monotonic() < limit:
                        if process.poll() is not None:
                            raise RuntimeError(f"{mode}: guest exited before dashboard")
                        text = capture(label)
                        # Dark VGA headings can be missed. Require independent markers.
                        ready = "PROGRESS" in text and "TAB" in text
                        # Firmware can retain the old framebuffer while rebooting.
                        # The pre-reset Messages view is not a newly started app.
                        if ready and (not fresh or ("SUMMARY" in text and "ORIGINAL MACHINE" not in text)):
                            return text
                        time.sleep(5)
                    raise RuntimeError(f"{mode}: dashboard not found; inspect screenshot and OCR artifacts")

                dashboard = wait_dashboard("dashboard")
                if not all(marker in dashboard for marker in ("DETAILS", "REPORTS", "MESSAGES")):
                    raise RuntimeError(f"{mode}: secondary navigation is not readable on the boot console")
                if usb_image:
                    if "PERSISTEN" not in dashboard:
                        raise RuntimeError(f"{mode}: boot USB report partition not mounted")
                    for _ in range(3):
                        command("human-monitor-command", {"command-line": "sendkey tab"})
                        time.sleep(0.3)
                    wait_screen("reports-before-reboot", lambda text: "PERSISTENCE CHECK" in text)
                    # A report from another laptop must be blocked before authentication.
                    for _ in range(7):
                        command("human-monitor-command", {"command-line": "sendkey down"})
                        time.sleep(0.3)
                    command("human-monitor-command", {"command-line": "sendkey ret"})
                    time.sleep(2)
                    command("human-monitor-command", {"command-line": "sendkey ret"})
                    time.sleep(2)
                    wait_screen("wrong-machine-retry", lambda text: "ORIGINAL MACHINE" in text)
                    command("system_reset")
                    time.sleep(10)
                    if "PERSISTEN" not in wait_dashboard("dashboard-after-reboot", fresh=True):
                        raise RuntimeError(f"{mode}: persistent storage unavailable after reboot")
                    for _ in range(3):
                        command("human-monitor-command", {"command-line": "sendkey tab"})
                        time.sleep(0.3)
                    wait_screen("reports-after-reboot", lambda text: "PERSISTENCE CHECK" in text)
                    print(f"PASS: {mode} USB storage, saved report recovery after reboot, and original-machine retry guard", flush=True)
                    continue_exit = True
                else:
                    continue_exit = False
                if not continue_exit:
                    command("human-monitor-command", {"command-line": "sendkey tab"})
                    time.sleep(2)
                    wait_screen("details", lambda text: "DETAILS" in text or "NO SCAN DATA" in text)
                    command("human-monitor-command", {"command-line": "sendkey ret"})
                    wait_screen("activation-required", lambda text: "ACTIVATION REQUIRED" in text)
                    for _ in range(2):
                        command("human-monitor-command", {"command-line": "sendkey down"})
                        time.sleep(0.3)
                    command("human-monitor-command", {"command-line": "sendkey ret"})
                    wait_screen("activation-cancelled", lambda text: "TESTS BLOCKED" in text)
                    print(f"PASS: {mode} requires activation before Full QC and cancellation blocks tests", flush=True)
                command("human-monitor-command", {"command-line": "sendkey q"})
                time.sleep(3)
                wait_screen("exit", lambda text: "EXITED" in text)
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
