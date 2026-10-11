namespace LaptopQC.Core.Diagnostics;

public class WindowsActivationStatus
{
    public bool IsActivated { get; set; }
    public int? LicenseStatus { get; set; }
    public string StatusLabel { get; set; } = "Unknown";
    public string? ProductName { get; set; }
    public string? Description { get; set; }
    public string? PartialProductKey { get; set; }
    public string? Error { get; set; }

    public string Summary
    {
        get
        {
            var key = string.IsNullOrWhiteSpace(PartialProductKey) ? "" : $" (...{PartialProductKey})";
            return $"{StatusLabel}{key}".Trim();
        }
    }
}

public class AntivirusProductInfo
{
    public string Name { get; set; } = "Unknown";
    public string? ProductStateHex { get; set; }
}

public class AntivirusStatus
{
    public bool HasAnyProduct { get; set; }
    public bool IsHealthy { get; set; }
    public string OverallHealth { get; set; } = "Unknown";
    public List<AntivirusProductInfo> Products { get; set; } = new();
    public string? Error { get; set; }

    public string Summary
    {
        get
        {
            if (Products.Count == 0)
                return OverallHealth;

            var names = string.Join(", ", Products.Select(p => p.Name));
            return $"{OverallHealth} ({names})";
        }
    }
}
