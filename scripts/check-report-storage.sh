#!/usr/bin/env bash
set -eu
SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)

# Mock the OS boundary: these checks never mount, format, or alter a real drive.
for scenario in missing_boot missing_label ambiguous wrong_disk wrong_fs mount_fail read_only success already_mounted; do
    (
        export SCENARIO="$scenario"
        findmnt() {
            if [[ "$*" == *"/run/live/medium"* ]]; then
                [[ "$SCENARIO" != missing_boot ]] && echo /dev/boot1
            elif [[ "$SCENARIO" == already_mounted ]]; then echo /run/pramaan-data
            fi
            return 0
        }
        readlink() { printf '%s\n' "${@: -1}"; }
        lsblk() {
            if [[ -z "${@: -1}" ]]; then return 0; fi
            if [[ "$SCENARIO" == wrong_disk && "${@: -1}" == /dev/boot2 ]]; then echo '/dev/internal disk'
            else echo '/dev/boot disk'; fi
        }
        blkid() {
            if [[ "$*" == *LABEL=* ]]; then
                case "$SCENARIO" in
                    missing_label) return 0;;
                    ambiguous) printf '/dev/boot2\n/dev/other2\n';;
                    *) echo /dev/boot2;;
                esac
            elif [[ "$SCENARIO" == wrong_fs ]]; then echo vfat
            else echo ext4; fi
        }
        mountpoint() { return 1; }
        mount() { [[ "$SCENARIO" != mount_fail ]]; }
        mkdir() { return 0; }
        chmod() { return 0; }
        mktemp() { [[ "$SCENARIO" != read_only ]] && echo /run/pramaan-data/reports/.mock; }
        rm() { return 0; }
        # The directory existence check is mocked by using a real scratch directory
        # for successful cases; no block device is involved.
        if [[ "$SCENARIO" == success || "$SCENARIO" == already_mounted || "$SCENARIO" == read_only ]]; then
            function findmnt() {
                if [[ "$*" == *"/run/live/medium"* ]]; then echo /dev/boot1
                else echo "$SCRIPT_DIR"; fi
            }
            # Ensure mktemp models success without creating files.
        fi
        source "$SCRIPT_DIR/iso-assets/pramaan-report-storage.sh"
        if [[ "$SCENARIO" == success || "$SCENARIO" == already_mounted ]]; then
            [[ "$PRAMAAN_REPORTS_PERSISTENT" == 1 && "$PRAMAAN_REPORTS_DIR" == "$SCRIPT_DIR/reports" ]]
        else
            [[ "$PRAMAAN_REPORTS_PERSISTENT" == 0 && -z "${PRAMAAN_REPORTS_DIR:-}" ]]
        fi
    )
    echo "PASS: $scenario"
done
