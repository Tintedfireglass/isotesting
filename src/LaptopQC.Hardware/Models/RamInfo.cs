namespace LaptopQC.Hardware.Models;

/// <summary>
/// Represents a single RAM module
/// </summary>
public class RamModule
{
    public string Manufacturer { get; set; } = string.Empty;
    public string PartNumber { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public long CapacityGB { get; set; }
    public int SpeedMHz { get; set; }
    public string MemoryType { get; set; } = string.Empty;
    public string FormFactor { get; set; } = string.Empty;
    public int Slot { get; set; }
}

/// <summary>
/// Represents overall RAM information
/// </summary>
public class RamInfo
{
    public long TotalCapacityGB { get; set; }
    public int TotalSlots { get; set; }
    public int UsedSlots { get; set; }
    public List<RamModule> Modules { get; set; } = new();
}
