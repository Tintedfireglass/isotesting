"""Prepare a disposable ISO-backed USB image and test real ext4 persistence."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

iso = next(Path("download").rglob("*.iso")).resolve()
payload = next(Path("render-evidence").rglob("payload.json")).read_text()
data = json.loads(payload)


def run(*args):
    return subprocess.check_output(args, text=True).strip()


with tempfile.TemporaryDirectory(prefix="pramaan-usb-") as scratch:
    scratch = Path(scratch)
    image = scratch / "boot.raw"
    shutil.copyfile(iso, image)
    with image.open("ab") as stream:
        stream.truncate(image.stat().st_size + 512 * 1024 * 1024)
    run("sudo", "sgdisk", "--move-second-header", str(image))
    run("sudo", "sgdisk", "--new=5:0:0", "--typecode=5:8300", "--change-name=5:PRAMAAN_DATA", str(image))
    loop = run("sudo", "losetup", "--find", "--show", "--partscan", str(image))
    mount = scratch / "mount"
    mount.mkdir()
    mounted = False
    try:
        # Only format partition 5 of the loop device backed by this generated image.
        backing = run("sudo", "losetup", "--noheadings", "--output", "BACK-FILE", loop)
        if not loop.startswith("/dev/loop") or Path(backing).resolve() != image.resolve():
            raise RuntimeError("Loop device is not backed by the disposable USB image")
        run("sudo", "udevadm", "settle")
        partition = loop + "p5"
        run("sudo", "mkfs.ext4", "-F", "-L", "PRAMAAN_DATA", partition)
        run("sudo", "mount", partition, str(mount))
        mounted = True
        envelope = {"Version": 1, "ReportId": data["reportId"], "MachineId": data["machineId"],
                    "HealthId": data["healthId"], "Payload": payload, "Status": "Pending",
                    "SavedAtUtc": data["timestamp"], "LastError": "PERSISTENCE CHECK"}
        fixture = scratch / "saved-report.json"
        fixture.write_text(json.dumps(envelope))
        run("sudo", "install", "-d", "-m", "700", str(mount / "reports"))
        run("sudo", "install", "-m", "600", str(fixture), str(mount / "reports" / (data["reportId"] + ".json")))
        run("sudo", "sync")
    finally:
        if mounted:
            run("sudo", "umount", str(mount))
        run("sudo", "losetup", "--detach", loop)
    subprocess.run([sys.executable, "scripts/iso-smoke.py", str(iso)],
                   env={**os.environ, "PRAMAAN_USB_IMAGE": str(image)}, check=True)
