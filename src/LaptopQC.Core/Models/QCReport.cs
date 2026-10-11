using LaptopQC.Hardware.Models;
using LaptopQC.Core.Diagnostics;

namespace LaptopQC.Core.Models;

public class QCReport
{
    public string ReportId { get; set; } = Guid.NewGuid().ToString();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string RefurbishId { get; set; } = "";
    public string TechnicianNotes { get; set; } = "";
    public string TechnicianId { get; set; } = "";
    public string PhysicalCondition { get; set; } = "";
    public string ScratchesAndDents { get; set; } = "";
    public string AppVersion { get; set; } = "";

    // System Information
    public SystemInfo? SystemInfo { get; set; }
    public string MacAddress { get; set; } = "";
    public int DeviceId { get; set; } = 0;

    // Test Results
    public TestResult CpuTest { get; set; } = new();
    public TestResult RamTest { get; set; } = new();
    public TestResult StorageTest { get; set; } = new();
    public TestResult BatteryTest { get; set; } = new();
    public TestResult KeyboardTest { get; set; } = new();
    public TestResult TrackpadTest { get; set; } = new();
    public TestResult DisplayTest { get; set; } = new();
    public TestResult UsbTest { get; set; } = new();
    public TestResult AudioVideoTest { get; set; } = new();
    public TestResult AudioJackTest { get; set; } = new();
    public TestResult SmartTest { get; set; } = new();
    public TestResult GpuTest { get; set; } = new();
    public TestResult NetworkTest { get; set; } = new();
    public TestResult DisplayPortTest { get; set; } = new();
    public TestResult BluetoothTest { get; set; } = new();
    public TestResult ChargerTest { get; set; } = new();

    // Detailed Info (Snapshots)
    public CpuInfo? CpuDetails { get; set; }
    public RamInfo? RamDetails { get; set; }
    public StorageInfo? StorageDetails { get; set; }
    public BatteryInfo? BatteryDetails { get; set; }
    public DevicesInfo? DeviceDetails { get; set; }

    // Grading
    public int OverallScore { get; set; }
    public string OverallGrade { get; set; } = "–";

    // PRAMAAN standardized scoring
    public PramaanResult? PramaanResult { get; set; }

    /// <summary>Unique, tamper-proof ID for the specific QC run and certification</summary>
    public string HealthId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>SHA-256 hash of the full QC Report to prevent tampering with records</summary>
    public string DiagnosticHash { get; set; } = string.Empty;

    /// <summary>
    /// Backward compatibility: a device with grade D or better (score >= 50) is considered sellable.
    /// </summary>
    public bool OverallPass => OverallScore >= 50;
}

public class TestResult
{
    public bool Tested { get; set; }
    public bool Passed { get; set; }
    public string Message { get; set; } = "Not Run";
    public List<string> Details { get; set; } = new();
    public List<string>? KeyboardMissingKeys { get; set; }
    public DateTime Timestamp { get; set; }

    // Grading
    public int Score { get; set; }
    public string Grade { get; set; } = "–";
}
