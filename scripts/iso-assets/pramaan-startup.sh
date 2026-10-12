#!/usr/bin/env bash
# =============================================================================
#  pramaan-startup.sh — Boot Startup Script for Pramaan Live USB
#
#  Sequence:
#    1. Show splash / branding
#    2. Attempt ethernet DHCP auto-connect (via NetworkManager)
#    3. If no internet → offer nmtui Wi-Fi or skip
#    4. Launch pramaan in a loop (restart for next laptop without rebooting)
# =============================================================================

# The boot virtual console supports the ANSI palette, not xterm's 256 colors.
export TERM=linux
export LANG=en_US.UTF-8

# ── Colour helpers ────────────────────────────────────────────────────────────
BOLD="\033[1m"
DIM="\033[2m"
CYAN="\033[36m"
PURPLE="\033[35m"
GREEN="\033[32m"
YELLOW="\033[33m"
RED="\033[31m"
RESET="\033[0m"

clear

# ── ASCII splash ──────────────────────────────────────────────────────────────
echo -e "${BOLD}${PURPLE}"
echo "  ██████╗ ██████╗  █████╗ ███╗   ███╗ █████╗  █████╗ ███╗   ██╗"
echo "  ██╔══██╗██╔══██╗██╔══██╗████╗ ████║██╔══██╗██╔══██╗████╗  ██║"
echo "  ██████╔╝██████╔╝███████║██╔████╔██║███████║███████║██╔██╗ ██║"
echo "  ██╔═══╝ ██╔══██╗██╔══██║██║╚██╔╝██║██╔══██║██╔══██║██║╚██╗██║"
echo "  ██║     ██║  ██║██║  ██║██║ ╚═╝ ██║██║  ██║██║  ██║██║ ╚████║"
echo "  ╚═╝     ╚═╝  ╚═╝╚═╝  ╚═╝╚═╝     ╚═╝╚═╝  ╚═╝╚═╝  ╚═╝╚═╝  ╚═══╝"
echo -e "${RESET}"
echo -e "  ${BOLD}QC Diagnostics Tool${RESET}  ${DIM}|  Live USB Edition${RESET}"
echo -e "  ${DIM}──────────────────────────────────────────────────────────${RESET}"
echo ""

# ── Helper: check internet connectivity ──────────────────────────────────────
check_internet() {
    # Try two DNS servers in parallel for speed
    ping -c 1 -W 3 8.8.8.8 &>/dev/null || \
    ping -c 1 -W 3 1.1.1.1 &>/dev/null
}

# ── Step 1: Network ───────────────────────────────────────────────────────────
echo -e "  ${BOLD}[ 1 / 2 ]${RESET}  Network connection"
echo ""

# Ensure NetworkManager is running
if ! systemctl is-active --quiet NetworkManager 2>/dev/null; then
    echo -e "  Starting NetworkManager..."
    systemctl start NetworkManager
fi

# Give NM a few seconds to auto-connect ethernet via DHCP
echo -e "  ${DIM}Checking for ethernet / auto-connect...${RESET}"
for i in 1 2 3 4 5; do
    sleep 1
    if check_internet; then
        break
    fi
done

if check_internet; then
    # Show which interface connected
    IFACE=$(ip route get 8.8.8.8 2>/dev/null | awk '{for(i=1;i<=NF;i++) if($i=="dev") print $(i+1)}' | head -1)
    echo -e "  ${GREEN}✓  Connected${RESET}${DIM} via ${IFACE:-ethernet}${RESET}"
else
    echo -e "  ${YELLOW}⚠  No automatic connection detected.${RESET}"
    echo ""
    echo -e "  ${DIM}How would you like to connect?${RESET}"
    echo ""
    echo -e "    ${BOLD}[1]${RESET}  Connect to Wi-Fi  ${DIM}(opens network manager)${RESET}"
    echo -e "    ${BOLD}[2]${RESET}  Plug in ethernet and retry"
    echo -e "    ${BOLD}[3]${RESET}  Skip — continue without internet"
    echo ""
    read -r -t 60 -p "  Your choice [1/2/3] (auto-skip in 60s): " net_choice || net_choice="3"
    echo ""

    case "${net_choice}" in
        1)
            # nmtui provides a clean TUI for Wi-Fi
            nmtui connect
            sleep 3
            if check_internet; then
                echo -e "  ${GREEN}✓  Connected via Wi-Fi${RESET}"
            else
                echo -e "  ${YELLOW}⚠  Still no internet. Some features may be limited.${RESET}"
            fi
            ;;
        2)
            echo -e "  ${DIM}Retrying ethernet in 5 seconds...${RESET}"
            sleep 5
            if check_internet; then
                echo -e "  ${GREEN}✓  Connected${RESET}"
            else
                echo -e "  ${YELLOW}⚠  No connection. Continuing without internet.${RESET}"
            fi
            ;;
        *)
            echo -e "  ${YELLOW}⚠  Skipping network. Continuing without internet.${RESET}"
            ;;
    esac
fi

echo ""
echo -e "  ${DIM}──────────────────────────────────────────────────────────${RESET}"
echo ""

# ── Step 2: Launch Pramaan ────────────────────────────────────────────────────
source /usr/local/bin/pramaan-report-storage.sh
echo -e "  ${BOLD}[ 2 / 2 ]${RESET}  Starting Pramaan..."
sleep 1
clear

# ── App loop ──────────────────────────────────────────────────────────────────
# After each QC run completes, offer to run the next one without rebooting.
while true; do
    /usr/local/bin/pramaan
    EXIT_CODE=$?

    # Preserve fatal runtime output (for example, a missing library) instead of
    # immediately clearing it before the technician has a chance to read it.
    if [ "$EXIT_CODE" -ne 0 ]; then
        echo ""
        echo -e "  ${RED}Pramaan exited unexpectedly (exit code: ${EXIT_CODE}).${RESET}"
        echo -e "  ${DIM}Press ENTER to return to the launcher, or S for a debugging shell.${RESET}"
        read -r -p "  > " crash_choice
        if [ "${crash_choice,,}" = "s" ]; then
            bash --login
        fi
    fi

    clear
    echo ""
    echo -e "  ${BOLD}${PURPLE}Pramaan exited${RESET}  ${DIM}(exit code: ${EXIT_CODE})${RESET}"
    echo ""
    echo -e "  ${DIM}──────────────────────────────────────────────────────────${RESET}"
    echo ""
    echo -e "  ${BOLD}[ENTER]${RESET}  Start next QC run  ${DIM}(plug in the next laptop)${RESET}"
    echo -e "  ${BOLD}[S]${RESET}      Open a shell       ${DIM}(advanced / debugging)${RESET}"
    echo -e "  ${BOLD}[R]${RESET}      Reboot this machine"
    echo ""
    read -r -t 120 -p "  > " post_choice || post_choice=""
    echo ""

    case "${post_choice,,}" in
        s)
            echo -e "  ${YELLOW}Opening shell. Type '${BOLD}exit${RESET}${YELLOW}' to return to Pramaan.${RESET}"
            echo ""
            bash --login
            clear
            ;;
        r)
            echo -e "  Rebooting..."
            sleep 1
            reboot
            ;;
        *)
            # Default / Enter / timeout → restart pramaan for next laptop
            clear
            continue
            ;;
    esac
done
