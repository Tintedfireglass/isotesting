#!/usr/bin/env bash
# Run only inside the disposable QEMU guest, via a read-only test-script share.
set -euo pipefail
export PAGER=cat NM_CLI_PAGER=cat TERM=dumb
exec > /dev/ttyS0 2>&1
trap 'echo "WIFI_QA_FAIL at line $LINENO"' ERR
echo WIFI_QA_START
test -f /lib/firmware/regulatory.db
echo 'PASS: wireless regulatory database resolves inside the live system'
modprobe mac80211_hwsim radios=2
cleanup() {
    nmcli con delete Pramaan-QA-client Pramaan-QA-ap || true
    modprobe -r mac80211_hwsim || true
}
trap cleanup EXIT
rfkill unblock wifi
nmcli radio wifi on
sleep 3
mapfile -t radios < <(iw dev | awk '$1 == "Interface" {print $2}')
[[ ${#radios[@]} == 2 ]]
ap=${radios[0]}
client=${radios[1]}
# Fixed addresses avoid requiring a DHCP server in the shipping image. This
# checks scanning and an actual WPA2 association through its installed supplicant.
nmcli con add type wifi ifname "$ap" con-name Pramaan-QA-ap ssid Pramaan-QA \
    wifi.mode ap wifi.band bg wifi.channel 6 wifi-sec.key-mgmt wpa-psk \
    wifi-sec.psk qa-password-only wifi-sec.proto rsn ipv4.method manual ipv4.addresses 192.168.77.1/24 \
    ipv4.never-default yes ipv6.method disabled
nmcli --wait 30 con up Pramaan-QA-ap
# NetworkManager's scan request is asynchronous. Wait for the new AP instead
# of treating the initial empty scan cache as a connection failure.
found=0
for attempt in $(seq 1 15); do
    nmcli device wifi rescan ifname "$client" || true
    sleep 2
    if nmcli -g SSID device wifi list ifname "$client" --rescan no | grep -Fx Pramaan-QA; then
        found=1
        break
    fi
done
nmcli device wifi list ifname "$client" --rescan no
if [[ "$found" != 1 ]]; then
    iw reg get
    nmcli device status
    journalctl -u NetworkManager --no-pager -n 50
    echo 'WIFI_QA_FAIL: test access point was not discovered within 30 seconds'
    exit 1
fi
nmcli con add type wifi ifname "$client" con-name Pramaan-QA-client ssid Pramaan-QA \
    wifi-sec.key-mgmt wpa-psk wifi-sec.psk qa-password-only wifi-sec.proto rsn \
    ipv4.method manual ipv4.addresses 192.168.77.2/24 ipv4.never-default yes ipv6.method disabled
nmcli --wait 30 con up Pramaan-QA-client
iw dev "$client" link
iw dev "$client" link | grep 'SSID: Pramaan-QA'
[[ $(nmcli -g GENERAL.STATE device show "$client") == '100 (connected)' ]]
echo WIFI_QA_PASS
