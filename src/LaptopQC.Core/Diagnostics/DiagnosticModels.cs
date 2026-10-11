namespace LaptopQC.Core.Diagnostics;

// ──────────────────────────────────────────────────────────────
// Shared diagnostic model classes
// 
// These were originally defined alongside their Windows 
// diagnostic implementations. Extracted here so both Windows 
// and macOS implementations can reference the same models.
// ──────────────────────────────────────────────────────────────

// ═══════════════ Storage Models ═══════════════

public class StorageInfo
{
    public List<StorageDevice> Devices { get; set; } = new();
    public List<StorageVolume> Volumes { get; set; } = new();
    public double TotalCapacityGB => Devices.Sum(d => d.SizeGB);
    public bool IsTampered { get; set; }
    public string TamperReason { get; set; } = "";
    public bool IsInconclusive { get; set; }
    public string InconclusiveReason { get; set; } = "";
    public bool IsSuspicious { get; set; }
    public string SuspiciousReason { get; set; } = "";
    public List<RaidArrayInfo> RaidArrays { get; set; } = new();
    /// <summary>Number of disk-error events (IDs 7/11/51) found in System log over last 30 days.</summary>
    public int RaidDiskErrorEventCount { get; set; }
    /// <summary>Human-readable detail lines from each RAID health layer (event log, passthrough, etc.).</summary>
    public List<string> RaidHealthDetails { get; set; } = new();
}

public class RaidArrayInfo
{
    public string Name { get; set; } = "";
    public string Level { get; set; } = "";
    public string State { get; set; } = "Active";
    public int ActiveDrives { get; set; }
    public int TotalDrives { get; set; }
    /// <summary>e.g. "storage-spaces", "megaraid", "hp-smart-array", "intel-rst", "unknown-raid"</summary>
    public string ControllerType { get; set; } = "";
    public bool IsHealthy { get; set; } = true;
    public string HealthStatus { get; set; } = "";
    public double TotalSizeGB { get; set; }
    public List<string> MemberLocations { get; set; } = new();
}

public class StorageVolume
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string FileSystem { get; set; } = "";
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public long UsedBytes { get; set; }
    public double UsedPercent { get; set; }
}

public class StorageDevice
{
    public string Model { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string InterfaceType { get; set; } = "";
    public string MediaType { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public ulong SizeBytes { get; set; }
    public double SizeGB { get; set; }
    public bool IsSsd { get; set; }
    public bool IsEMMC { get; set; }

    // RAID flags — set when this entry represents a RAID virtual disk or member drive.
    // When IsRaid=true, SMART telemetry absence is NOT treated as tampered/inconclusive.
    public bool IsRaid { get; set; }
    public string RaidControllerType { get; set; } = "";
    
    // SMART data
    public int? HealthPercent { get; set; }
    public int? Temperature { get; set; }
    public int? PowerOnHours { get; set; }
    public long? TotalBytesWritten { get; set; }
    public bool IsTampered { get; set; }
    public string TamperReason { get; set; } = "";
    public bool IsInconclusive { get; set; }
    public string InconclusiveReason { get; set; } = "";
    public bool IsSuspicious { get; set; }
    public string SuspiciousReason { get; set; } = "";
}

// ═══════════════ Battery Models ═══════════════

public class BatteryInfo
{
    public bool IsPresent { get; set; }
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public string BatteryStatus { get; set; } = "";
    public int EstimatedChargeRemaining { get; set; }
    public int ChargePercent { get; set; }
    public string PartNumber { get; set; } = "";
    
    // Capacity info (in mWh)
    public uint DesignedCapacityMWh { get; set; }
    public uint FullChargedCapacityMWh { get; set; }
    
    // Manufacturer info
    public string ManufactureName { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string Chemistry { get; set; } = "";
    
    // Health metrics
    public int? WearLevelPercent { get; set; }
    public int? HealthPercent { get; set; }
    public int? CycleCount { get; set; }

    /// <summary>
    /// True when battery firmware/BMS data appears invalid/unreliable (e.g. impossible capacities).
    /// In this case, battery diagnostics should fail and reports should include a disclaimer.
    /// </summary>
    public bool IsTampered { get; set; }

    /// <summary>Optional human-readable reason for IsTampered.</summary>
    public string TamperReason { get; set; } = "";
}

// NOTE: SystemInfo, CpuInfo, RamInfo, RamModule are in LaptopQC.Hardware.Models
// Do NOT duplicate them here.

// ═══════════════ SMART Test Models ═══════════════

public class SmartDriveInfo
{
    public string DevicePath { get; set; } = "";
    public string DeviceType { get; set; } = "";
    public string Model { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string Serial { get; set; } = "";
    public int HealthScore { get; set; }
    public bool HealthPassed { get; set; }
    public int? Temperature { get; set; }
    public int? PowerOnHours { get; set; }
    public List<string> Warnings { get; set; } = new();
#if WINDOWS
    public LaptopQC.Hardware.Providers.SmartData? SmartData { get; set; }
#endif
    
    public string HealthStatus => HealthScore switch
    {
        >= 90 => "Excellent",
        >= 70 => "Good",
        >= 50 => "Fair",
        >= 25 => "Poor",
        _ => "Critical"
    };
}

public class SmartTestProgress
{
    public string DevicePath { get; set; } = "";
    public string Status { get; set; } = "";
    public int PercentComplete { get; set; }
    public bool IsRunning { get; set; }
}

public class SmartTestResultInfo
{
    public string DevicePath { get; set; } = "";
    public string TestType { get; set; } = "";
    public bool Success { get; set; }
    public bool Passed { get; set; }
    public bool Skipped { get; set; }
    public string Message { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration => EndTime - StartTime;
#if WINDOWS
    public LaptopQC.Hardware.Providers.SmartData? PostTestData { get; set; }
#endif
}

public class SmartHealthCheckResult
{
    public DateTime CheckTime { get; set; }
    public bool SmartctlAvailable { get; set; }
    public bool OverallHealthy { get; set; }
    public string Message { get; set; } = "";
    public List<SmartDriveInfo> Devices { get; set; } = new();
}

// Stress test models — simplified for macOS. Windows uses richer versions
// defined inside the #if WINDOWS-guarded CpuStressTest.cs / GpuStressTest.cs.
#if !WINDOWS

public class StressTestProgress
{
    public int PercentComplete { get; set; }
    public string Status { get; set; } = "";
    public double? Temperature { get; set; }
    public double? ClockSpeed { get; set; }
}

public class CpuStressResult
{
    public bool Passed { get; set; }
    public string Message { get; set; } = "";
    public double? MaxTemperature { get; set; }
    public bool ThermalThrottle { get; set; }
}

public class GpuStressProgress
{
    public int PercentComplete { get; set; }
    public string Status { get; set; } = "";
    public double? Temperature { get; set; }
    public double? Load { get; set; }
    public double? ClockSpeed { get; set; }
}

public class GpuStressResult
{
    public bool Passed { get; set; }
    public string Message { get; set; } = "";
    public bool GpuDetected { get; set; }
}
#endif
