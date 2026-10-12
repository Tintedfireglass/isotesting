# Linux app validation

The regression runner is `scripts/LinuxAppChecks/LinuxAppChecks.csproj`. It uses
populated reports and a controlled HTTP handler; it never uploads a customer report.

Covered automatically:

- Render bounds at 80×24, 100×32, 140×45, 160×50 and additional edge sizes,
  with ASCII and Unicode borders, long details, paging and navigation.
- Real Linux pseudo-terminal resizing while the CLI runs.
- Serialized condition choices and omitted optional values. The companion
  `check-contract.ts` validates the fixture against the dashboard's submit schema.
- Successful responses, validation and authentication failures, duplicates and
  timeouts, with unchanged payloads on retry.
- App restart recovery, interrupted temporary files, corrupt report files,
  storage unavailable before sending and after server acceptance.
- Linux file permissions and launcher partition identification/fallback paths.

GitHub Actions builds the self-contained CLI and ISO, then boots BIOS and UEFI
QEMU guests. Screenshots and OCR evidence are retained as workflow artifacts.
The USB validation workflow creates only a disposable disk image with an ext4
`PRAMAAN_DATA` partition and checks report recovery after reboot and rejection of
a retry on another machine.

Physical laptop boot, hardware diagnostics and a real authenticated submission
remain manual acceptance checks. No real license, trial account or customer report
is used by the automated tests. See `linux-live-usb.md` for preparation and controls.
