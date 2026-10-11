#!/usr/bin/env bash
# Sourced by the launcher. Never format disks or select the tested laptop's disk.
export PRAMAAN_REPORTS_PERSISTENT=0
unset PRAMAAN_REPORTS_DIR

pramaan_report_storage() {
    local boot_source boot_disk candidate candidate_disk data_mount probe
    local -a candidates
    boot_source=$(findmnt -nro SOURCE /run/live/medium 2>/dev/null)
    boot_source=$(readlink -f "${boot_source}" 2>/dev/null)
    boot_disk=$(lsblk -sno PATH,TYPE "${boot_source}" 2>/dev/null | awk '$2 == "disk" {print $1; exit}')
    if [[ -z "${boot_disk}" ]]; then
        echo "Report storage: boot USB could not be identified; using temporary local storage."
        return
    fi
    mapfile -t candidates < <(blkid -t LABEL=PRAMAAN_DATA -o device 2>/dev/null)
    if [[ ${#candidates[@]} -ne 1 ]]; then
        echo "Report storage: expected one PRAMAAN_DATA partition; reports will not survive reboot."
        return
    fi
    candidate=$(readlink -f "${candidates[0]}")
    candidate_disk=$(lsblk -sno PATH,TYPE "${candidate}" 2>/dev/null | awk '$2 == "disk" {print $1; exit}')
    if [[ "${candidate_disk}" != "${boot_disk}" || $(blkid -s TYPE -o value "${candidate}") != ext4 ]]; then
        echo "Report storage: PRAMAAN_DATA must be ext4 on the boot USB; using temporary storage."
        return
    fi
    data_mount=$(findmnt -nro TARGET -S "${candidate}" 2>/dev/null)
    if [[ -z "${data_mount}" ]]; then
        data_mount=/run/pramaan-data
        mkdir -p "${data_mount}" || return
        if mountpoint -q "${data_mount}" || ! mount -t ext4 -o rw,nosuid,nodev,noexec "${candidate}" "${data_mount}"; then
            echo "Report storage: unable to mount PRAMAAN_DATA; reports will not survive reboot."
            return
        fi
    fi
    if [[ ! -d "${data_mount}" || -L "${data_mount}/reports" ]]; then
        echo "Report storage: invalid report directory; using temporary storage."
        return
    fi
    if ! mkdir -p "${data_mount}/reports" || ! chmod 700 "${data_mount}/reports"; then
        echo "Report storage: report directory is unwritable; reports will not survive reboot."
        return
    fi
    probe=$(mktemp "${data_mount}/reports/.write-check.XXXXXX") || {
        echo "Report storage: write check failed; reports will not survive reboot."
        return
    }
    rm -f -- "${probe}"
    export PRAMAAN_REPORTS_DIR="${data_mount}/reports"
    export PRAMAAN_REPORTS_PERSISTENT=1
    echo "Report storage: persistent on PRAMAAN_DATA."
}

pramaan_report_storage
