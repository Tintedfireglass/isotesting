#if WINDOWS
using LaptopQC.Core.Abstractions;
using LaptopQC.Core.Services;
using LaptopQC.Hardware.Providers;
using System.Management;
using System.IO;

namespace LaptopQC.Core.Diagnostics;

/// <summary>
/// Provides storage device detection and SMART health diagnostics
/// </summary>
public class StorageDiagnostic : IStorageDiagnostic
{
    private readonly IWmiProvider _wmi;
    private readonly ISensorProvider? _sensors;

    public StorageDiagnostic(IWmiProvider? wmiProvider = null, ISensorProvider? sensors = null)
    {
        _wmi = wmiProvider ?? new WmiProvider();
        _sensors = sensors;
    }

    /// <summary>
    /// Gets information about all storage devices
    /// </summary>
    public StorageInfo GetInfo()
    {
        var info = new StorageInfo();
        
        // Get physical disks
        foreach (var obj in _wmi.Query("Win32_DiskDrive"))
        {
            var device = new StorageDevice
            {
                Model = _wmi.GetValue<string>(obj, "Model", "Unknown") ?? "Unknown",
                SerialNumber = _wmi.GetValue<string>(obj, "SerialNumber", "")?.Trim() ?? "",
                InterfaceType = _wmi.GetValue<string>(obj, "InterfaceType", "Unknown") ?? "Unknown",
                MediaType = _wmi.GetValue<string>(obj, "MediaType", "")?.Trim() ?? "",
                SizeBytes = _wmi.GetValue<ulong>(obj, "Size", 0),
                DeviceId = _wmi.GetValue<string>(obj, "DeviceID", "") ?? ""
            };

            // Determine if SSD, HDD, or eMMC
            device.IsEMMC = device.Model.Contains("emmc", StringComparison.OrdinalIgnoreCase) || 
                            device.MediaType.Contains("emmc", StringComparison.OrdinalIgnoreCase) || 
                            device.InterfaceType.Equals("MMC", StringComparison.OrdinalIgnoreCase);
            device.IsSsd = device.IsEMMC || DetectSsd(device);
            device.SizeGB = device.SizeBytes / (1024.0 * 1024 * 1024);

            // Flag RAID virtual disks early so tamper logic can be bypassed later
            bool isRaidInterface = device.InterfaceType.Equals("RAID", StringComparison.OrdinalIgnoreCase);
            bool isRaidModel = device.Model.Contains("RAID", StringComparison.OrdinalIgnoreCase) ||
                               device.Model.Contains("MegaRAID", StringComparison.OrdinalIgnoreCase) ||
                               device.Model.Contains("Smart Array", StringComparison.OrdinalIgnoreCase) ||
                               device.Model.Contains("Intel RST", StringComparison.OrdinalIgnoreCase);
            if (isRaidInterface || isRaidModel)
            {
                device.IsRaid = true;
                device.RaidControllerType = isRaidModel ? InferControllerFromModel(device.Model) : "unknown-raid";
            }

            // Skip removable media (USB flash drives, SD cards, external drives).
            // These never report SMART health data and would incorrectly penalize the score.
            bool isRemovable = device.MediaType.Contains("Removable", StringComparison.OrdinalIgnoreCase) ||
                               device.InterfaceType.Equals("USB", StringComparison.OrdinalIgnoreCase);
            if (isRemovable)
                continue;

            info.Devices.Add(device);
        }

        // ── RAID Detection ─────────────────────────────────────────────────────
        // Run the RAID health service. On non-RAID machines this returns immediately
        // with IsRaidDetected=false and causes zero behavioural change.
        var raidHealth = new RaidHealthService().DetectAndAssess();
        if (raidHealth.IsRaidDetected)
        {
            info.RaidArrays.AddRange(raidHealth.Arrays);
            info.RaidDiskErrorEventCount = raidHealth.DiskErrorEventCount;
            info.RaidHealthDetails.AddRange(raidHealth.Details);

            // If WMI returned no recognisable physical drives (controller hides them),
            // synthesise a virtual disk entry so the report isn't blank.
            if (info.Devices.Count == 0 && raidHealth.Arrays.Count > 0)
            {
                var arr = raidHealth.Arrays[0];
                info.Devices.Add(new StorageDevice
                {
                    Model = $"RAID Volume ({arr.Level})",
                    SerialNumber = "",
                    InterfaceType = "RAID",
                    MediaType = "RAID Virtual Disk",
                    SizeGB = arr.TotalSizeGB,
                    SizeBytes = (ulong)(arr.TotalSizeGB * 1024 * 1024 * 1024),
                    IsSsd = true,
                    IsRaid = true,
                    RaidControllerType = raidHealth.ControllerType,
                    DeviceId = "RAID-VIRTUAL-0",
                });
            }
            else
            {
                // Mark any SCSI-interface drives as RAID (they're virtual disks from a RAID controller)
                foreach (var dev in info.Devices.Where(d =>
                    !d.IsRaid &&
                    d.InterfaceType.Equals("SCSI", StringComparison.OrdinalIgnoreCase)))
                {
                    dev.IsRaid = true;
                    dev.RaidControllerType = raidHealth.ControllerType;
                }
            }
        }
        // ──────────────────────────────────────────────────────────────────────

        // Get logical drive usage (used / free)
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                if (drive.DriveType != DriveType.Fixed) continue;

                var total = drive.TotalSize;
                var free = drive.TotalFreeSpace;
                var used = total - free;
                var usedPercent = total > 0 ? (used / (double)total) * 100.0 : 0;

                info.Volumes.Add(new StorageVolume
                {
                    Name = drive.Name.TrimEnd('\\'),
                    Label = drive.VolumeLabel ?? "",
                    FileSystem = drive.DriveFormat ?? "",
                    TotalBytes = total,
                    FreeBytes = free,
                    UsedBytes = used,
                    UsedPercent = usedPercent
                });
            }
        }
        catch { /* Best-effort only */ }

        // Get SMART data from LibreHardwareMonitor
        try
        {
            if (_sensors != null)
            {
                EnrichWithSmartData(info, _sensors);
            }
            else
            {
                using var sensors = new SensorProvider();
                sensors.Initialize();
                EnrichWithSmartData(info, sensors);
            }
        }
        catch { /* Ignore SMART failures */ }

        EvaluateTamperState(info);

        return info;
    }

    private static string InferControllerFromModel(string model)
    {
        var m = model.ToLowerInvariant();
        if (m.Contains("megaraid") || m.Contains("perc")) return "megaraid";
        if (m.Contains("smart array") || m.Contains("hp")) return "hp-smart-array";
        if (m.Contains("intel rst") || m.Contains("intel rapid")) return "intel-rst";
        return "unknown-raid";
    }

    private bool DetectSsd(StorageDevice device)
    {
        var model = device.Model.ToLower();
        var mediaType = device.MediaType.ToLower();
        
        // SSD indicators
        if (mediaType.Contains("ssd") || mediaType.Contains("solid"))
            return true;
        if (model.Contains("ssd") || model.Contains("nvme") || model.Contains("m.2"))
            return true;
        if (device.InterfaceType.Equals("NVMe", StringComparison.OrdinalIgnoreCase))
            return true;
            
        // HDD indicators
        if (mediaType.Contains("hdd") || mediaType.Contains("hard"))
            return false;
            
        // Default: assume SSD for modern systems
        return true;
    }

    private void EnrichWithSmartData(StorageInfo info, ISensorProvider sensors)
    {
        // LibreHardwareMonitor populates storage SMART data
        // We access it through the sensor interface
        foreach (var device in info.Devices)
        {
            var smartData = sensors.GetStorageHealth(device.Model);
            if (smartData != null)
            {
                device.HealthPercent = smartData.HealthPercent;
                device.Temperature = smartData.Temperature;
                device.PowerOnHours = smartData.PowerOnHours;
                device.TotalBytesWritten = smartData.TotalBytesWritten;
            }
        }
    }

    /// <summary>
    /// Validates storage health
    /// </summary>
    public (bool IsHealthy, string Message) ValidateStorage(StorageInfo info)
    {
        if (info.Devices.Count == 0)
        {
            // If RAID arrays were detected even with no WMI physical drives, treat as healthy
            if (info.RaidArrays.Count > 0 && info.RaidArrays.All(a => a.IsHealthy))
                return (true, $"RAID array detected and healthy ({info.RaidArrays[0].Level})");
            if (info.RaidArrays.Count > 0 && info.RaidArrays.Any(a => !a.IsHealthy))
                return (false, $"RAID array degraded or failed");
            return (false, "No storage devices detected");
        }

        if (info.IsTampered)
            return (false, string.IsNullOrWhiteSpace(info.TamperReason) ? "Storage Tampered - Unable to read data" : info.TamperReason);

        if (info.IsInconclusive)
        {
            // If RAID arrays are healthy, override the inconclusive verdict
            if (info.RaidArrays.Count > 0 && info.RaidArrays.All(a => a.IsHealthy))
                return (true, $"RAID array detected and healthy ({info.RaidArrays[0].Level})");
            return (false, string.IsNullOrWhiteSpace(info.InconclusiveReason) ? "Storage Inconclusive - Unable to verify health data" : info.InconclusiveReason);
        }

        // RAID-specific pass/fail based on array health
        if (info.RaidArrays.Count > 0)
        {
            bool raidHealthy = info.RaidArrays.All(a => a.IsHealthy);
            if (!raidHealthy)
                return (false, "RAID array degraded or failed — check member drives");
        }

        foreach (var device in info.Devices)
        {
            if (device.IsRaid) continue; // health already assessed at array level above

            if (device.HealthPercent.HasValue && device.HealthPercent < 50)
                return (false, $"Drive health critical: {device.Model} at {device.HealthPercent}%");
                
            if (device.Temperature.HasValue && device.Temperature > 60)
                return (false, $"Drive temperature high: {device.Model} at {device.Temperature}°C");
        }

        if (info.IsSuspicious)
            return (true, string.IsNullOrWhiteSpace(info.SuspiciousReason) ? "Storage data suspicious - Review recommended" : info.SuspiciousReason);

        int physicalCount = info.Devices.Count(d => !d.IsRaid);
        int raidCount = info.RaidArrays.Count;
        if (physicalCount > 0 && raidCount > 0)
            return (true, $"{physicalCount} drive(s) healthy + {raidCount} RAID array(s) healthy");
        if (raidCount > 0)
            return (true, $"{raidCount} RAID array(s) healthy");
        return (true, $"{physicalCount} drive(s) healthy");
    }

    private static void EvaluateTamperState(StorageInfo info)
    {
        bool allMissingSmartTelemetry = info.Devices.Count > 0;
        foreach (var device in info.Devices)
        {
            bool hasSmartTelemetry = device.HealthPercent.HasValue || device.Temperature.HasValue || device.PowerOnHours.HasValue || device.TotalBytesWritten.HasValue;
            
            // ── RAID bypass ────────────────────────────────────────────────────────
            // RAID virtual disks expose controller-generated metadata, not real SMART
            // telemetry. Missing telemetry is expected and must NOT be flagged as
            // tampered or inconclusive. Skip all tamper checks for RAID entries.
            if (device.IsRaid)
            {
                allMissingSmartTelemetry = false; // don't count RAID disks as "missing"
                continue;
            }
            // ──────────────────────────────────────────────────────────────────────

            // Skip eMMC drives when checking for missing telemetry since they rarely support SMART
            if (!device.IsEMMC)
            {
                allMissingSmartTelemetry &= !hasSmartTelemetry;
            }
            else if (info.Devices.Count == 1)
            {
                // If the only drive is eMMC, it is definitely not "missing/inconclusive"
                allMissingSmartTelemetry = false;
            }

            bool invalidHealth = device.HealthPercent.HasValue && (device.HealthPercent.Value < 0 || device.HealthPercent.Value > 100);
            bool invalidTemp = device.Temperature.HasValue && (device.Temperature.Value < -10 || device.Temperature.Value > 120);
            bool invalidPowerHours = device.PowerOnHours.HasValue && device.PowerOnHours.Value < 0;
            bool invalidSize = device.SizeGB <= 0;

            // Anti-Tamper: High bytes/data written but practically 0 power on hours
            bool tbwParadox = device.TotalBytesWritten.HasValue && device.TotalBytesWritten.Value > 100 && 
                              device.PowerOnHours.HasValue && device.PowerOnHours.Value < 10;

            // Anti-Tamper: Faked OEM strings / generic bulk flashing
            string sn = (device.SerialNumber ?? "").Trim();
            bool genericSerial = sn == "000000000000" || sn == "11111111" || sn == "1234567890" || (sn.Length > 0 && sn.Replace("0", "").Length == 0);
            
            string mod = (device.Model ?? "").ToLowerInvariant();
            bool isGenericModel = mod.Equals("ssd") || mod.Equals("nvme") || mod.Contains("sata ssd") || mod.Contains("nvme ssd");
            bool fakeTemp = device.IsSsd && device.Temperature.HasValue && device.Temperature.Value == 40 && isGenericModel;

            if (invalidHealth || invalidTemp || invalidPowerHours || invalidSize || tbwParadox || genericSerial || fakeTemp)
            {
                device.IsTampered = true;
                device.TamperReason = tbwParadox ? "SMART Tampered - Improbable TBW/Power Ratio" :
                                      genericSerial ? "Storage Tampered - Counterfeit Serial Number" :
                                      fakeTemp ? "Sensor Tampered - Hardcoded Thermal Output" :
                                      "Storage Tampered - Inconsistent drive telemetry";
                
                info.IsTampered = true;
                // Keep the first tamper reason found
                if (string.IsNullOrWhiteSpace(info.TamperReason))
                    info.TamperReason = device.TamperReason;
            }

            bool suspiciousPlaceholderTemp =
                device.Temperature.HasValue &&
                device.Temperature.Value == 0 &&
                device.PowerOnHours.HasValue &&
                device.PowerOnHours.Value > 1000;
            bool suspiciousPerfectHealth =
                device.HealthPercent.HasValue &&
                device.HealthPercent.Value == 100 &&
                device.PowerOnHours.HasValue &&
                device.PowerOnHours.Value > 20000;

            if (!info.IsTampered && (suspiciousPlaceholderTemp || suspiciousPerfectHealth))
            {
                device.IsSuspicious = true;
                device.SuspiciousReason = "Storage data suspicious - Review recommended";
                info.IsSuspicious = true;
                if (string.IsNullOrWhiteSpace(info.SuspiciousReason))
                    info.SuspiciousReason = "Storage data suspicious - Review recommended";
            }
        }

        // Only set inconclusive when there are physical (non-RAID) devices missing telemetry.
        // If every device is a RAID virtual disk, allMissingSmartTelemetry will be false (reset above).
        if (!info.IsTampered && allMissingSmartTelemetry)
        {
            // Don't mark inconclusive when RAID arrays were detected — they provide their own health signal.
            bool raidProvidesCoverage = info.RaidArrays.Count > 0;
            if (!raidProvidesCoverage)
            {
                info.IsInconclusive = true;
                info.InconclusiveReason = "Storage Inconclusive - Unable to verify health data";
                foreach (var device in info.Devices)
                {
                    device.IsInconclusive = true;
                    device.InconclusiveReason = info.InconclusiveReason;
                }
            }
        }
    }
}
#endif

