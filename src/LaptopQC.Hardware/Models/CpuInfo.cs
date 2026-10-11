namespace LaptopQC.Hardware.Models;

/// <summary>
/// Represents CPU information
/// </summary>
public class CpuInfo
{
    public string Name { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public int Cores { get; set; }
    public int Threads { get; set; }
    public int MaxClockSpeedMHz { get; set; }
    public int CurrentClockSpeedMHz { get; set; }
    public string Architecture { get; set; } = string.Empty;
    public string ProcessorId { get; set; } = string.Empty;
    public int L2CacheSizeKB { get; set; }
    public int L3CacheSizeKB { get; set; }
    public double? TemperatureCelsius { get; set; }
}
