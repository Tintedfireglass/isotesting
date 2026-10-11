namespace LaptopQC.Hardware.Providers;

public interface ISmartctlProvider
{
    bool IsAvailable { get; }
    string? FindSmartctlPath();
    List<SmartctlDrive> ScanDrives();
    SmartData? GetSmartData(string devicePath, string? deviceType = null);
    SmartTestResult StartShortTest(string devicePath, string? deviceType = null);
    SmartTestStatus GetTestStatus(string devicePath, string? deviceType = null);
    List<SmartTestLogEntry> GetTestLog(string devicePath, string? deviceType = null);
}
