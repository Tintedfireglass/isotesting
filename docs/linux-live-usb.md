# Linux live USB: responsive UI and saved reports

The dashboard reflows according to terminal columns and rows. Use arrows and Enter
for actions, Tab for Summary / Details / System / Reports / Messages, and Page Up /
Page Down for full content. Q or Escape exits when idle. Tests continue while views
are switched. Full QC asks for physical condition and scratches/dents.

Completed Full QC payloads are saved before upload. Open **Saved reports / Retry**
to retry without repeating tests. Retry requires the original laptop and fresh
license or trial authentication. Credentials are never saved with reports.
Submitted reports remain stored. A duplicate response is retained for verification
using its health ID; it does not generate another report. Messages shows full errors.

## Set up persistent report storage

The live ISO itself is read-only. Its default writable overlay is temporary.
Flashing the ISO alone does **not** provide persistent report storage.

1. Back up the USB's existing data before preparing it. Flash the newly built ISO.
2. On a Linux preparation machine, identify the boot USB by model, serial, and
   capacity in a partition editor. Confirm it is the USB, not an internal disk.
3. If the USB has usable unallocated space, create an ext4 partition there and label
   it exactly `PRAMAAN_DATA`. Preserve the ISO's existing boot partitions. Do not
   proceed if the editor would overwrite them or if no usable space is available;
   use a larger USB or suitable USB preparation tooling instead.
4. Boot the USB. The launcher verifies that there is exactly one matching partition,
   that it is ext4, and that it belongs to the boot medium. It mounts it with
   `nosuid,nodev,noexec`, creates an owner-only `reports` directory, and checks writes.
5. Confirm the dashboard says **USB report storage: persistent**. Run a QC, reboot,
   and verify that the report remains in Saved reports.

The launcher never creates, formats, or repartitions drives. Missing, ambiguous,
read-only, or incorrectly located storage falls back to local storage with a visible
message that reports will be lost on reboot. Authentication stays in the temporary
machine-local session. Do not unplug the boot USB while the app is running; shut
down cleanly before moving it. Report files contain hardware identifiers and notes.

On an installed Linux system, reports default to the application's configuration
directory under `SavedReports`, which survives application restarts. An explicitly
configured `PRAMAAN_REPORTS_DIR` can override the report directory; the persistence
indicator is set by the live launcher using `PRAMAAN_REPORTS_PERSISTENT=1` only after
its storage checks succeed.

## Build and acceptance

Build `cli/Pramaan.CLI.csproj`; publish linux-x64 through the existing ISO workflow.
Run `scripts/LinuxAppChecks/LinuxAppChecks.csproj` for rendering, upload, and storage
regressions. Run `scripts/check-report-storage.sh` under Bash for launcher tests.

Before distributing an ISO, boot it in a Linux VM and then a physical laptop.
Verify both UEFI and legacy boot, 80x24 and larger consoles, window/font resizing,
Full QC submission with an authorized test account, no-network recovery, report
survival after app restart and reboot, and refusal to retry on another laptop.
