#!/usr/bin/env bash
# Fault injection inside a disposable live-ISO VM only.
set -euo pipefail
export PAGER=cat TERM=dumb LC_ALL=C
exec > /dev/ttyS0 2>&1
trap 'echo "NETWORK_SOAK_FAIL at line $LINENO"' ERR
source /usr/local/bin/pramaan-network.sh
saved_dns=$(mktemp)
cat /etc/resolv.conf > "$saved_dns"
saved_epoch=$(date +%s)
saved_seconds=$SECONDS
clock_changed=0
cleanup() {
    cat "$saved_dns" > /etc/resolv.conf
    rm -f "$saved_dns"
    if [[ "$clock_changed" == 1 ]]; then
        date -u -s "@$((saved_epoch + SECONDS - saved_seconds))"
        timedatectl set-ntp true
    fi
}
trap cleanup EXIT
require_online() {
    for attempt in $(seq 1 5); do
        if pramaan_network_check; then return; fi
        sleep 1
    done
    echo "$PRAMAAN_NETWORK_ERROR"
    return 1
}
for cycle in 1 2 3; do
    nmcli networking off
    if pramaan_network_check; then echo 'NETWORK_SOAK_FAIL: offline check passed'; exit 1; fi
    echo "PASS: cycle $cycle detects offline state: $PRAMAAN_NETWORK_ERROR"
    pramaan_network_prepare
    pramaan_ethernet_connect
    require_online
    echo "PASS: Ethernet DHCP/DNS/HTTPS recovery cycle $cycle"
done
# A dead DNS server must produce an error, then recover after restoring DNS.
cat /etc/resolv.conf > "$saved_dns"
printf 'nameserver 192.0.2.1\noptions timeout:1 attempts:1\n' > /etc/resolv.conf
if pramaan_network_check; then echo 'NETWORK_SOAK_FAIL: bad DNS was accepted'; exit 1; fi
echo "PASS: broken DNS is rejected: $PRAMAAN_NETWORK_ERROR"
cat "$saved_dns" > /etc/resolv.conf
require_online
echo 'PASS: HTTPS recovers after restoring DNS'
# Prove certificate validation actually rejects an invalid system date.
saved_epoch=$(date +%s)
saved_seconds=$SECONDS
timedatectl set-ntp false
clock_changed=1
date -u -s '2020-01-01 00:00:00'
if pramaan_network_check; then echo 'NETWORK_SOAK_FAIL: invalid-date TLS was accepted'; exit 1; fi
[[ "$PRAMAAN_NETWORK_ERROR" == *certificate* ]]
echo "PASS: invalid-clock certificate validation fails: $PRAMAAN_NETWORK_ERROR"
date -u -s "@$((saved_epoch + SECONDS - saved_seconds))"
timedatectl set-ntp true
clock_changed=0
require_online
echo 'PASS: verified HTTPS recovers after restoring clock'
ip route show default
cat /etc/resolv.conf
echo NETWORK_SOAK_PASS
