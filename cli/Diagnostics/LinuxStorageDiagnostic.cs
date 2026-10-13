using System.Text.Json;
using System.Text.RegularExpressions;
using LaptopQC.Core.Abstractions;
using LaptopQC.Core.Diagnostics;

namespace Pramaan.CLI.Diagnostics;

public class LinuxStorageDiagnostic : IStorageDiagnostic
{
    public StorageInfo GetInfo()
    {
        var info = new StorageInfo();

        try
        {
            var json = LinuxCommandRunner.Run("lsblk", "-J -b -o NAME,SIZE,TYPE,MODEL,ROTA,MOUNTPOINT,SERIAL,TRAN,RM");
            info = ParseDevices(json);
        }
        catch
        {
            // Do not grade guessed devices from /proc/partitions as tested storage.
            info.IsInconclusive = true;
            info.InconclusiveReason = "Unable to enumerate internal storage with lsblk.";
        }
        CheckSoftwareRaid(info);

        return info;
    }

    public static StorageInfo ParseDevices(string json)
    {
        var info = new StorageInfo();
        using var doc = JsonDocument.Parse(json);
        foreach (var dev in doc.RootElement.GetProperty("blockdevices").EnumerateArray())
        {
            string Read(string key) => dev.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString().Trim() : "";
            var name = Read("name");
            if (Read("type") != "disk" || name.StartsWith("loop") || name.StartsWith("ram") || name.StartsWith("zram")) continue;
            if (Read("tran").Equals("usb", StringComparison.OrdinalIgnoreCase) || Read("rm") is "1" or "True" or "true" || IsBootMedium(dev)) continue;
            long.TryParse(Read("size"), out long bytes);
            var model = Read("model");
            info.Devices.Add(new StorageDevice
            {
                DeviceId = name.StartsWith("/dev/") ? name : "/dev/" + name,
                Model = string.IsNullOrWhiteSpace(model) ? name.ToUpperInvariant() : model,
                SerialNumber = Read("serial"), SizeGB = bytes / (1024.0 * 1024 * 1024),
                IsSsd = Read("rota") is "0" or "false" or "False"
            });
        }
        return info;
    }

    static bool IsBootMedium(JsonElement dev)
    {
        if (dev.TryGetProperty("mountpoint", out var mp) && mp.ValueKind == JsonValueKind.String
            && mp.GetString() is "/run/live/medium" or "/lib/live/mount/medium") return true;
        return dev.TryGetProperty("children", out var children) && children.EnumerateArray().Any(IsBootMedium);
    }
    public (bool IsHealthy, string Message) ValidateStorage(StorageInfo info)
    {
        EvaluateTamperState(info);
        if (info.Devices.Count == 0) return (false, "No storage devices detected");
        if (info.IsTampered) return (false, string.IsNullOrWhiteSpace(info.TamperReason) ? "Storage Tampered" : info.TamperReason);
        if (info.IsInconclusive) return (false, string.IsNullOrWhiteSpace(info.InconclusiveReason) ? "Storage health could not be determined" : info.InconclusiveReason);
        
        var degradedArray = info.RaidArrays.FirstOrDefault(r => r.State == "Degraded");
        if (degradedArray != null) return (false, $"Storage health warning: Degraded RAID array detected ({degradedArray.Name})");
        
        if (info.IsSuspicious) return (true, "Storage data suspicious — review recommended");
        return (true, $"{info.Devices.Count} drive(s) detected, {info.TotalCapacityGB:F0}GB total");
    }

    private static void EvaluateTamperState(StorageInfo info)
    {
        info.IsTampered = false;
        info.IsInconclusive = false;
        info.TamperReason = null;
        info.InconclusiveReason = null;
        
        bool hasHealthData = false;
        foreach (var device in info.Devices)
        {
            if (device.HealthPercent.HasValue) hasHealthData = true;
            if (device.SizeGB <= 0)
            {
                device.IsTampered = true;
                info.IsTampered = true;
                info.TamperReason = "Storage Tampered - Unable to read data";
            }
        }

        if (!hasHealthData && info.Devices.Count > 0)
        {
            info.IsInconclusive = true;
            info.InconclusiveReason = "Storage health inconclusive — run with sudo to enable SMART";
        }
    }

    private void CheckSoftwareRaid(StorageInfo info)
    {
        if (!System.IO.File.Exists("/proc/mdstat")) return;

        try
        {
            var mdstat = LinuxCommandRunner.ReadFile("/proc/mdstat");
            // Regex pattern to extract md devices and active states
            // Example: md0 : active raid1 sda1[0] sdb1[1]
            var matches = Regex.Matches(mdstat, @"(md\d+)\s+:\s+(\w+)\s+(raid\d+)\s+(.*)");
            foreach (Match match in matches)
            {
                var name = match.Groups[1].Value;
                var state = match.Groups[2].Value;
                var level = match.Groups[3].Value;
                var disks = match.Groups[4].Value;

                var activeCount = Regex.Matches(disks, @"\[\d+\]").Count;

                var raidInfo = new RaidArrayInfo
                {
                    Name = name,
                    Level = level.ToUpperInvariant(),
                    State = state.Equals("active", StringComparison.OrdinalIgnoreCase) ? "Healthy" : "Degraded",
                    ActiveDrives = activeCount,
                    TotalDrives = activeCount
                };

                // If degraded state is found in the block status line (e.g., [U_])
                var statusLineMatch = Regex.Match(mdstat, name + @"[\s\S]*?\[([U_]+)\]");
                if (statusLineMatch.Success)
                {
                    var statusStr = statusLineMatch.Groups[1].Value;
                    raidInfo.TotalDrives = statusStr.Length;
                    raidInfo.ActiveDrives = statusStr.Count(c => c == 'U');
                    if (statusStr.Contains('_'))
                    {
                        raidInfo.State = "Degraded";
                        info.IsSuspicious = true; // Flag array anomaly
                    }
                }

                info.RaidArrays.Add(raidInfo);
            }
        }
        catch { /* Ignore parsing errors */ }
    }
}
