namespace LaptopQC.Hardware.Models;

/// <summary>
/// Contains all system hardware information
/// </summary>
public class SystemInfo
{
    public string ComputerName { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string? ProductVersion { get; set; }
    public string? SystemFamily { get; set; }
    public string SerialNumber { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string BiosVersion { get; set; } = string.Empty;
    public string BiosManufacturer { get; set; } = string.Empty;
    public DateTime? BiosReleaseDate { get; set; }
    public string OsVersion { get; set; } = string.Empty;
    public string WindowsProductName { get; set; } = string.Empty;
    public string WindowsActivationStatus { get; set; } = string.Empty;
    public bool? IsWindowsActivated { get; set; }
    public DateTime? WindowsLastUpdatedAt { get; set; }
    public string AntivirusStatus { get; set; } = string.Empty;
    public bool? IsAntivirusHealthy { get; set; }
    public DateTime ScanTimestamp { get; set; } = DateTime.UtcNow;
}
