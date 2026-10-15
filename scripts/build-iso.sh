#!/usr/bin/env bash
# =============================================================================
#  build-iso.sh — Pramaan QC Tool Live USB ISO Builder
#  Runs on: Ubuntu (GitHub Actions runner or local Linux)
#  Output:  pramaan-live.iso  (UEFI + Legacy BIOS, hybrid GPT/MBR)
# =============================================================================
set -euo pipefail

# ── Paths ────────────────────────────────────────────────────────────────────
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
CHROOT="${REPO_ROOT}/chroot"
ISO_DIR="${REPO_ROOT}/iso-build"
OUTPUT_ISO="${REPO_ROOT}/pramaan-live.iso"
ASSETS_DIR="${SCRIPT_DIR}/iso-assets"
PRAMAAN_BIN="${REPO_ROOT}/publish/pramaan/pramaan"

# ── Colours ──────────────────────────────────────────────────────────────────
BOLD="\033[1m"; CYAN="\033[36m"; GREEN="\033[32m"; YELLOW="\033[33m"; RESET="\033[0m"
step() { echo -e "\n${BOLD}${CYAN}==> $*${RESET}"; }
ok()   { echo -e "  ${GREEN}✓ $*${RESET}"; }
warn() { echo -e "  ${YELLOW}⚠ $*${RESET}"; }

# ── Preflight checks ─────────────────────────────────────────────────────────
step "Preflight checks"

if [[ ! -f "$PRAMAAN_BIN" ]]; then
    echo "ERROR: pramaan binary not found at: $PRAMAAN_BIN"
    echo "       Run 'dotnet publish' for linux-x64 first (the workflow does this)."
    exit 1
fi
ok "pramaan binary: $PRAMAAN_BIN ($(du -sh "$PRAMAAN_BIN" | cut -f1))"

if [[ "$(id -u)" -ne 0 ]]; then
    echo "ERROR: This script must be run as root (or via sudo)."
    exit 1
fi
ok "Running as root"

# Clean previous build artefacts
rm -rf "$CHROOT" "$ISO_DIR"

# ── Install host dependencies ─────────────────────────────────────────────────
step "Installing host build dependencies"
apt-get update -qq
apt-get install -y -qq \
    debootstrap \
    squashfs-tools \
    xorriso \
    grub-pc-bin \
    grub-efi-amd64-bin \
    mtools \
    isolinux
ok "Host deps installed"

# ── Bootstrap Debian Bookworm ─────────────────────────────────────────────────
step "Bootstrapping minimal Debian Bookworm chroot (~5 min)"
debootstrap \
    --arch=amd64 \
    --variant=minbase \
    bookworm \
    "$CHROOT" \
    http://deb.debian.org/debian
ok "Debian Bookworm base created"

# ── Mount virtual filesystems ─────────────────────────────────────────────────
step "Mounting virtual filesystems"
mount -t proc  /proc     "$CHROOT/proc"
mount -t sysfs /sys      "$CHROOT/sys"
mount -o bind  /dev      "$CHROOT/dev"
mount -o bind  /dev/pts  "$CHROOT/dev/pts"

# Always unmount on exit (even on error)
cleanup() {
    echo -e "\n${BOLD}==> Unmounting virtual filesystems${RESET}"
    umount "$CHROOT/dev/pts" 2>/dev/null || true
    umount "$CHROOT/dev"     2>/dev/null || true
    umount "$CHROOT/sys"     2>/dev/null || true
    umount "$CHROOT/proc"    2>/dev/null || true
}
trap cleanup EXIT

# ── Configure apt ─────────────────────────────────────────────────────────────
step "Configuring apt sources"
cat > "$CHROOT/etc/apt/sources.list" << 'APT_EOF'
deb http://deb.debian.org/debian bookworm main contrib non-free non-free-firmware
deb http://security.debian.org/debian-security bookworm-security main contrib
APT_EOF

# Prevent interactive prompts during package installs
export DEBIAN_FRONTEND=noninteractive
chroot "$CHROOT" apt-get update -qq

# ── Install packages ──────────────────────────────────────────────────────────
step "Installing packages (kernel, networking, live-boot — ~10 min)"
chroot "$CHROOT" apt-get install -y --no-install-recommends \
    linux-image-amd64 \
    live-boot \
    live-boot-initramfs-tools \
    systemd \
    systemd-sysv \
    dbus \
    network-manager \
    wpasupplicant \
    rfkill \
    iw \
    systemd-timesyncd \
    firmware-iwlwifi \
    firmware-sof-signed \
    firmware-realtek \
    firmware-atheros \
    firmware-brcm80211 \
    firmware-libertas \
    firmware-amd-graphics \
    firmware-misc-nonfree \
    alsa-utils \
    pulseaudio-utils \
    v4l-utils \
    ethtool \
    lm-sensors \
    bluez \
    ca-certificates \
    usbutils \
    pciutils \
    dmidecode \
    util-linux \
    iproute2 \
    iputils-ping \
    curl \
    locales \
    console-setup \
    kbd \
    sudo \
    bash \
    smartmontools \
    libicu72 \
    libstdc++6
ok "Packages installed"

# Live-boot/ifupdown must not claim adapters before NetworkManager can manage them.
mkdir -p "$CHROOT/etc/NetworkManager/conf.d"
cat > "$CHROOT/etc/NetworkManager/conf.d/90-pramaan-live.conf" << 'NM_EOF'
[main]
dns=default
rc-manager=file
[ifupdown]
managed=true
NM_EOF
# Do not retain the build machine's nameservers in the live image.
rm -f "$CHROOT/etc/resolv.conf"
ln -s /run/NetworkManager/resolv.conf "$CHROOT/etc/resolv.conf"

# ── Locale & hostname ─────────────────────────────────────────────────────────
step "Configuring locale and hostname"
chroot "$CHROOT" /bin/bash -c \
    "echo 'en_US.UTF-8 UTF-8' > /etc/locale.gen && locale-gen"
echo "LANG=en_US.UTF-8" > "$CHROOT/etc/default/locale"
echo "pramaan-qc" > "$CHROOT/etc/hostname"
cat > "$CHROOT/etc/hosts" << 'HOSTS_EOF'
127.0.0.1   localhost
127.0.1.1   pramaan-qc
HOSTS_EOF
ok "Locale: en_US.UTF-8 | Hostname: pramaan-qc"

# ── Install pramaan binary ─────────────────────────────────────────────────────
step "Installing pramaan binary"
cp "$PRAMAAN_BIN" "$CHROOT/usr/local/bin/pramaan"
chmod 755         "$CHROOT/usr/local/bin/pramaan"
ok "pramaan installed to /usr/local/bin/pramaan"

# ── Install startup script ─────────────────────────────────────────────────────
step "Installing startup script"
cp "$ASSETS_DIR/pramaan-startup.sh" "$CHROOT/usr/local/bin/pramaan-startup.sh"
install -m 755 "$ASSETS_DIR/pramaan-network.sh" "$CHROOT/usr/local/bin/pramaan-network.sh"
cp "$ASSETS_DIR/pramaan-report-storage.sh" "$CHROOT/usr/local/bin/pramaan-report-storage.sh"
chmod 755 "$CHROOT/usr/local/bin/pramaan-report-storage.sh"
chmod 755                            "$CHROOT/usr/local/bin/pramaan-startup.sh"
ok "pramaan-startup.sh installed"

# ── Configure autologin on tty1 ───────────────────────────────────────────────
step "Configuring autologin (root → tty1)"
mkdir -p "$CHROOT/etc/systemd/system/getty@tty1.service.d/"
cat > "$CHROOT/etc/systemd/system/getty@tty1.service.d/autologin.conf" << 'AUTOLOGIN_EOF'
[Service]
ExecStart=
ExecStart=-/sbin/agetty --autologin root --noclear %I $TERM
AUTOLOGIN_EOF

# ── Root bash profile → launch startup script ─────────────────────────────────
cat > "$CHROOT/root/.bash_profile" << 'PROFILE_EOF'
# Launch Pramaan automatically on tty1
export TERM=xterm-256color
export LANG=en_US.UTF-8
if [ "$(tty)" = "/dev/tty1" ]; then
    exec /usr/local/bin/pramaan-startup.sh
fi
PROFILE_EOF

# ── Enable / disable systemd services ─────────────────────────────────────────
step "Configuring systemd services"
chroot "$CHROOT" systemctl enable NetworkManager
chroot "$CHROOT" systemctl enable systemd-timesyncd
# Disable timers that generate network traffic and slow down boot
chroot "$CHROOT" systemctl disable apt-daily.timer            2>/dev/null || true
chroot "$CHROOT" systemctl disable apt-daily-upgrade.timer    2>/dev/null || true
chroot "$CHROOT" systemctl disable motd-news.timer            2>/dev/null || true
ok "NetworkManager enabled; noisy timers disabled"

# ── Live-boot config ──────────────────────────────────────────────────────────
# Tell live-boot to mount the filesystem.squashfs from the ISO
mkdir -p "$CHROOT/etc/live"
cat > "$CHROOT/etc/live/boot.conf" << 'LIVEBOOT_EOF'
# live-boot configuration
LIVE_BOOT_APPEND="quiet splash"
LIVEBOOT_EOF

# ── Cleanup chroot ────────────────────────────────────────────────────────────
step "Cleaning apt cache (reduces image size)"
chroot "$CHROOT" apt-get clean
rm -rf "$CHROOT/var/lib/apt/lists/"*
rm -rf "$CHROOT/tmp/"*
rm -rf "$CHROOT/var/log/"*.log 2>/dev/null || true
ok "Cache cleared"

# Unmount before squashfs
cleanup
trap - EXIT  # Disable the trap — we already cleaned up

# ── Build squashfs ────────────────────────────────────────────────────────────
step "Building filesystem.squashfs (xz compression — ~10 min)"
mkdir -p "$ISO_DIR/live"
mksquashfs "$CHROOT" "$ISO_DIR/live/filesystem.squashfs" \
    -comp xz \
    -noappend \
    -no-progress \
    -Xbcj x86
SQUASH_SIZE=$(du -sh "$ISO_DIR/live/filesystem.squashfs" | cut -f1)
ok "filesystem.squashfs: $SQUASH_SIZE"

# ── Copy kernel + initrd ──────────────────────────────────────────────────────
step "Copying kernel and initrd"
VMLINUZ=$(ls "$CHROOT/boot/vmlinuz-"* 2>/dev/null | sort -V | tail -1)
INITRD=$(ls  "$CHROOT/boot/initrd.img-"* 2>/dev/null | sort -V | tail -1)

if [[ -z "$VMLINUZ" || -z "$INITRD" ]]; then
    echo "ERROR: vmlinuz or initrd.img not found in chroot/boot/"
    ls "$CHROOT/boot/" || true
    exit 1
fi

cp "$VMLINUZ" "$ISO_DIR/live/vmlinuz"
cp "$INITRD"  "$ISO_DIR/live/initrd.img"
ok "Kernel: $(basename "$VMLINUZ")"
ok "Initrd: $(basename "$INITRD")"

# ── GRUB config ───────────────────────────────────────────────────────────────
step "Setting up GRUB"
mkdir -p "$ISO_DIR/boot/grub"
cp "$ASSETS_DIR/grub/grub.cfg" "$ISO_DIR/boot/grub/grub.cfg"
ok "GRUB config copied"

# ── Build ISO (UEFI + Legacy BIOS hybrid) ─────────────────────────────────────
# Note: avoid passing extra args via '--' to xorriso — flag syntax varies
# between xorriso versions and causes failures on the GitHub Actions runner.
step "Building final ISO (UEFI + Legacy BIOS)"
grub-mkrescue \
    --output="$OUTPUT_ISO" \
    "$ISO_DIR"

# ── Report ────────────────────────────────────────────────────────────────────
echo ""
echo -e "${BOLD}${GREEN}══════════════════════════════════════════${RESET}"
echo -e "${BOLD}${GREEN}  ISO Build Complete!${RESET}"
echo -e "${BOLD}${GREEN}══════════════════════════════════════════${RESET}"
echo -e "  Output : ${BOLD}$OUTPUT_ISO${RESET}"
echo -e "  Size   : ${BOLD}$(du -sh "$OUTPUT_ISO" | cut -f1)${RESET}"
echo ""
echo -e "  Flash to USB using Rufus (DD mode) or Balena Etcher."
echo -e "  Disable Secure Boot in BIOS before booting."
echo ""
