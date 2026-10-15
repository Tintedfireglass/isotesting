#!/usr/bin/env bash
# Network setup for the disposable live system. No passwords are logged.

pramaan_network_prepare() {
    systemctl start NetworkManager || return 1
    nmcli networking on || return 1
    rfkill unblock wifi 2>/dev/null || true
    nmcli radio wifi on || true
    # DHCP/DNS from the current network must replace build-host resolver settings.
    timedatectl set-ntp true 2>/dev/null || true
}

pramaan_ethernet_connect() {
    local iface kind state
    while IFS=: read -r iface kind state; do
        [[ "$kind" == ethernet && "$state" != connected ]] || continue
        nmcli device set "$iface" managed yes || continue
        nmcli --wait 20 device connect "$iface" || true
    done < <(nmcli -t -f DEVICE,TYPE,STATE device status)
}

pramaan_network_check() {
    # Test the actual TLS endpoint, including DNS and certificate validation.
    # Any HTTP response proves transport connectivity; authentication is separate.
    local code error_file
    error_file=$(mktemp) || return 1
    if code=$(curl --silent --show-error --output /dev/null --write-out '%{http_code}' \
        --connect-timeout 5 --max-time 12 https://pramaan-dashboard.gadgetguruz.com/ 2>"$error_file") &&
        [[ "$code" =~ ^[1-5][0-9][0-9]$ ]]; then
        PRAMAAN_NETWORK_ERROR=""
        rm -f "$error_file"
        return 0
    fi
    PRAMAAN_NETWORK_ERROR=$(cat "$error_file")
    rm -f "$error_file"
    return 1
}

pramaan_network_details() {
    echo ""
    echo "Network devices (unavailable can mean a missing driver, firmware or radio block):"
    nmcli -f DEVICE,TYPE,STATE,CONNECTION device status
    rfkill list 2>/dev/null || true
    echo ""
    echo "System time: $(date -u '+%Y-%m-%d %H:%M:%S UTC')"
    timedatectl show -p NTPSynchronized 2>/dev/null || true
    echo "Default route:"
    ip route show default
    echo "DNS:"
    cat /etc/resolv.conf
    echo ""
    if pramaan_network_check; then
        echo "Pramaan server reachable over verified HTTPS. Activation is still required."
    else
        echo "Pramaan HTTPS check failed: $PRAMAAN_NETWORK_ERROR"
        echo "Certificate errors: check the date/time, captive portal or network HTTPS inspection."
        echo "If Wi-Fi is hard-blocked, enable the laptop's wireless switch / airplane-mode key."
    fi
}

pramaan_network_menu() {
    local choice
    pramaan_network_prepare || echo "NetworkManager failed to start; inspect network details."
    while true; do
        echo ""
        echo "  [1] Connect / edit Wi-Fi or Ethernet"
        echo "  [2] Retry Ethernet (request an address automatically)"
        echo "  [3] Show network / HTTPS / clock details"
        echo "  [4] Return to Pramaan (tests require online activation)"
        read -r -p "  Your choice [1/2/3/4]: " choice || return
        case "$choice" in
            1) rfkill unblock wifi 2>/dev/null || true
               nmcli radio wifi on || true
               nmtui connect ;;
            2) pramaan_ethernet_connect ;;
            3) pramaan_network_details; continue ;;
            4|q|Q) return ;;
            *) continue ;;
        esac
        if pramaan_network_check; then
            echo "  Connected to Pramaan over verified HTTPS."
            return
        fi
        echo "  Connection is not ready: $PRAMAAN_NETWORK_ERROR"
    done
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    case "${1:-menu}" in
        check) pramaan_network_check ;;
        details) pramaan_network_details ;;
        auto) pramaan_network_prepare && pramaan_ethernet_connect && pramaan_network_check ;;
        *) pramaan_network_menu ;;
    esac
fi
