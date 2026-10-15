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
- Online activation failures, expired trials, Linux license platform selection,
  and machine-bound, owner-only credential hints that never authorize offline runs.
- Numeric/boolean lsblk fields, boot USB exclusion, NVMe namespace matching,
  wear-based SMART health and visible errors when SMART cannot be read.
- DNS, TLS and timeout failures in the network helper, explicit Ethernet
  activation and verified HTTPS connection checks.

The network validation workflow opens Settings before activation and returns to
the dashboard, disables networking inside the disposable guest, then verifies
Ethernet recovery, DHCP, DNS and verified HTTPS to the Pramaan server. It also
loads two `mac80211_hwsim` radios and verifies discovery and a WPA2 association
using the live image's installed NetworkManager and supplicant. The simulated
Wi-Fi profiles use fixed addresses; real Wi-Fi DHCP, radio/driver compatibility
and the tester's router still require a physical laptop retest. Test scripts are
provided to the guest through a read-only share and never alter host networking.

GitHub Actions builds the self-contained CLI and ISO, then boots BIOS and UEFI
QEMU guests. Screenshots and OCR evidence are retained as workflow artifacts.
The USB validation workflow creates only a disposable disk image with an ext4
`PRAMAAN_DATA` partition and checks report recovery after reboot and rejection of
a retry on another machine.

The normal regression suite uses fake credentials. A separate, explicitly authorized
live test used a real testing license in an isolated VM and uploaded one report
marked `CODEX-VM-QA`, with notes stating that it is synthetic hardware and that manual
components were not verified. The temporary Actions secret was removed afterward.
Physical laptop boot and the tester's actual SSD still require a hardware retest.
See `linux-live-usb.md` for preparation and controls.

## Latest network recovery ISO

- [Download `Pramaan_QC_Live_network-final_20261015.iso`](https://github.com/Tintedfireglass/isotesting/actions/runs/38770920517/artifacts/11990889622).
- [Final ISO network validation](https://github.com/Tintedfireglass/isotesting/actions/runs/38772478961): passed in BIOS and UEFI. Covers Settings, activation cancellation blocking tests, Ethernet recovery after disabling networking, DHCP/DNS, verified HTTPS, regulatory database availability, and simulated WPA2 association.
- CLI build and UI/report/activation/storage regressions passed. The ISO was built from source snapshot `2ddc6bd`. Its build run failed a post-build assertion because the host followed the regulatory database's absolute symlink outside the chroot. That assertion is corrected in source; the completed ISO was verified directly in the separate successful VM run linked above. No ISO rebuild is needed for this test-only correction.

This image supersedes the October 13 image below. Physical Wi-Fi/Ethernet, the
actual router and the tester's SSD still need a laptop smoke test. Open **Settings**
for connection setup and full network/HTTPS/clock details if activation fails.

## Previous activation and storage validation

- [Final ISO build, BIOS/UEFI boot and cancelled activation blocks QC](https://github.com/Tintedfireglass/isotesting/actions/runs/38341635926): passed.
- [Real Linux license activation, QA upload, virtual NVMe health and offline blocking after activation](https://github.com/Tintedfireglass/isotesting/actions/runs/38342662350): passed against that final ISO.
- [USB persistence, reboot recovery and original-machine retry guard](https://github.com/Tintedfireglass/isotesting/actions/runs/38340189973): passed in BIOS and UEFI on the storage-fix build. The subsequent final build changes licensing and machine-local credential hints; report persistence code is unchanged.
- [Download the final ISO artifact](https://github.com/Tintedfireglass/isotesting/actions/runs/38341635926/artifacts/11787437527).

The previous ISO filename is `Pramaan_QC_Live_linux-activation-storage-final_20261013.iso`; its date follows
the build runner's UTC clock. The application source was built from snapshot
`04cae12` in `Tintedfireglass/isotesting`. Earlier ISO artifacts are superseded.
