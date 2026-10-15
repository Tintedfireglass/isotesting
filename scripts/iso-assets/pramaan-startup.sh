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

# Network setup is reusable from Settings and the post-run launcher.
source /usr/local/bin/pramaan-network.sh
echo -e "  ${BOLD}[ 1 / 2 ]${RESET}  Network connection"
echo "  Preparing Wi-Fi and Ethernet..."
pramaan_network_prepare
pramaan_ethernet_connect
if pramaan_network_check; then
    echo "  Connected to Pramaan over verified HTTPS."
else
    echo "  Pramaan is not reachable: $PRAMAAN_NETWORK_ERROR"
    pramaan_network_menu
fi

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
            bash --noprofile --norc
        fi
    fi

    clear
    echo ""
    echo -e "  ${BOLD}${PURPLE}Pramaan exited${RESET}  ${DIM}(exit code: ${EXIT_CODE})${RESET}"
    echo ""
    echo -e "  ${DIM}──────────────────────────────────────────────────────────${RESET}"
    echo ""
    echo -e "  ${BOLD}[ENTER]${RESET}  Start next QC run  ${DIM}(plug in the next laptop)${RESET}"
    echo -e "  ${BOLD}[N]${RESET}      Network setup"
    echo -e "  ${BOLD}[S]${RESET}      Open a shell       ${DIM}(advanced / debugging)${RESET}"
    echo -e "  ${BOLD}[R]${RESET}      Reboot this machine"
    echo ""
    read -r -t 120 -p "  > " post_choice || post_choice=""
    echo ""

    case "${post_choice,,}" in
        n) pramaan_network_menu ;;
        s)
            echo -e "  ${YELLOW}Opening shell. Type '${BOLD}exit${RESET}${YELLOW}' to return to Pramaan.${RESET}"
            echo ""
            bash --noprofile --norc
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
