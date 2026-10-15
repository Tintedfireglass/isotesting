#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "$0")/iso-assets/pramaan-network.sh"

# Exercise the OS boundary without altering host network settings.
for scenario in online dns tls timeout; do
    (
        curl() {
            [[ "$*" == *https://pramaan-dashboard.gadgetguruz.com/* ]]
            [[ "$*" != *--insecure* ]]
            case "$scenario" in
                online) printf 403 ;; # Reachable even when authentication is required.
                dns) echo 'Could not resolve host' >&2; return 6 ;;
                tls) echo 'SSL certificate problem: certificate is not yet valid' >&2; return 60 ;;
                timeout) echo 'Connection timed out' >&2; return 28 ;;
            esac
        }
        if [[ "$scenario" == online ]]; then
            pramaan_network_check
            [[ -z "$PRAMAAN_NETWORK_ERROR" ]]
        else
            if pramaan_network_check; then echo "Unexpected success: $scenario"; exit 1; fi
            [[ -n "$PRAMAAN_NETWORK_ERROR" ]]
        fi
    )
done

calls=$(mktemp)
trap 'rm -f "$calls"' EXIT
nmcli() {
    if [[ "$*" == '-t -f DEVICE,TYPE,STATE device status' ]]; then
        printf 'enp1s0:ethernet:disconnected\nenp2s0:ethernet:connected\nwlan0:wifi:disconnected\n'
    else printf '%s\n' "$*" >> "$calls"; fi
}
pramaan_ethernet_connect
[[ "$(cat "$calls")" == $'device set enp1s0 managed yes\n--wait 20 device connect enp1s0' ]]
systemctl() { echo "systemctl $*" >> "$calls"; }
rfkill() { echo "rfkill $*" >> "$calls"; }
timedatectl() { echo "timedatectl $*" >> "$calls"; }
pramaan_network_prepare
grep -q 'radio wifi on' "$calls"
grep -q 'rfkill unblock wifi' "$calls"
grep -q 'timedatectl set-ntp true' "$calls"
echo 'PASS: verified HTTPS, HTTP auth responses, DNS/TLS/timeouts, radio enable and Ethernet DHCP retry'
