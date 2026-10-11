# Pramaan Linux ISO testing

Linux CLI and shared library source snapshot from the current GG-QCsoftware
workspace (base commit `a582bf234fcb095bb2362b420bb80e25b111789b`), including the
responsive terminal UI, report submission fix, saved-report recovery, and boot USB
report storage support. Windows app and dashboard source are excluded.

Run **Build Pramaan Live ISO** manually from Actions. The workflow runs Linux
regression checks, publishes the CLI, builds the Debian live ISO, and boots it under
BIOS and UEFI in disposable QEMU VMs. Download the ISO, terminal render snapshots,
and VM screenshots/OCR from the run artifacts.

See [live USB setup](docs/linux-live-usb.md) for `PRAMAAN_DATA` storage and technician
controls. Physical hardware QC and persistence across an actual USB reboot still
require a laptop acceptance test. No production reports are submitted by the build.
