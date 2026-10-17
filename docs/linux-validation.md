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
baseline Wi-Fi profiles use fixed addresses. The extended test provides a static
BusyBox DHCP server as guest test tooling and checks Wi-Fi DHCP,
incorrect passwords, repeated reconnects, software radio blocking and access-point
restart. It also injects broken DNS and an invalid clock inside the guest, verifies
HTTPS fails, restores them, and verifies recovery. Physical radio/driver
compatibility and the tester's router still require a laptop retest. Test scripts are
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

## Final compiled release — October 17, 2026

- [Download `Pramaan_QC_Live_final-tested_20261017.iso`](https://github.com/Tintedfireglass/isotesting/actions/runs/39050534971/artifacts/12107678080).
- Source snapshot: `bb2b15e`. ISO size: 484,167,680 bytes.
- SHA-256: `6e1a9e5c6ddabd0dd98839d954e81f59fdf2bf91549fdc38a2f602fc63d6c00e`.
- [Compilation and baseline validation](https://github.com/Tintedfireglass/isotesting/actions/runs/39050534971): passed, including Linux UI/report/authentication/storage regressions, real terminal resizing, dependency checks, BIOS/UEFI boot, Settings, activation blocking, Ethernet DHCP/DNS/HTTPS recovery and simulated WPA2 association.
- [Expanded network validation](https://github.com/Tintedfireglass/isotesting/actions/runs/39051377872): all three virtual Ethernet adapter jobs passed. Includes Wi-Fi DHCP reconnects, wrong-password rejection, software radio-block recovery, access-point restart, repeated Ethernet recovery, broken DNS and invalid-clock TLS rejection/recovery.
- [USB reboot recovery](https://github.com/Tintedfireglass/isotesting/actions/runs/39051376081): passed in BIOS and UEFI. Saved reports survive reboot on the configured PRAMAAN_DATA partition; original-machine retry restrictions are enforced.

These runs tested this freshly compiled ISO. It supersedes the earlier downloads
below. Physical laptop adapter/router compatibility and the tester's SSD still
require a hardware smoke test.

## October 15 network recovery ISO

- [Download `Pramaan_QC_Live_network-final_20261015.iso`](https://github.com/Tintedfireglass/isotesting/actions/runs/38770920517/artifacts/11990889622).
- [Final ISO network validation](https://github.com/Tintedfireglass/isotesting/actions/runs/38772478961): passed in BIOS and UEFI. Covers Settings, activation cancellation blocking tests, Ethernet recovery after disabling networking, DHCP/DNS, verified HTTPS, regulatory database availability, and simulated WPA2 association.
- [Final ISO USB recovery](https://github.com/Tintedfireglass/isotesting/actions/runs/38774807419): passed in BIOS and UEFI. Saved reports survive USB reboot and original-machine retry restrictions remain enforced.
- [Extended Wi-Fi and network fault tests](https://github.com/Tintedfireglass/isotesting/actions/runs/38776813626): passed against the same ISO. Incorrect WPA2 password is rejected; Wi-Fi obtains DHCP leases over three reconnects and recovers after software radio blocking and an access-point restart. Ethernet recovers DHCP/DNS/HTTPS over three disable/enable cycles. Broken DNS and an invalid system date correctly fail HTTPS; restoring DNS/time restores verified HTTPS. Serial logs contain DHCP acknowledgements and both `WIFI_QA_PASS` and `NETWORK_SOAK_PASS`.
- Additional virtual adapter checks passed: [VirtIO in UEFI](https://github.com/Tintedfireglass/isotesting/actions/runs/38774926781/job/116560005690) and [Realtek RTL8139 in BIOS](https://github.com/Tintedfireglass/isotesting/actions/runs/38774926781/job/116560005759). That matrix's initial extended Intel test failed because the test fixture renamed the BusyBox executable, preventing its DHCP applet from starting. The corrected extended test is the successful run linked above; no application change was required.
- Local UI, activation, SMART/storage, frozen-payload retries, atomic report recovery and launcher regressions passed again. The serialized report and all 18 physical-condition/scratches combinations satisfy the dashboard's actual submission schema; omitting optional condition fields also passes.
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
