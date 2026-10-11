using System.IO;
using System.Text.Json;
using LaptopQC.Core.Models;
using LaptopQC.Hardware.Models;

namespace Pramaan.CLI.UI;

public record RecentReportEntry(string Id, string Date, string Score, string Status);

/// <summary>Mutable shared state for the dashboard live view.</summary>
public class DashboardState
{
    public static readonly string[] MenuItems =
    {
        "1. Run Full QC (Recommended)",
        "2. Run Diagnostics",
        "3. Run Stress Tests",
        "4. Test USB Ports",
        "5. Test Ethernet",
        "6. View Results Table",
        "7. Settings",
        "8. Saved reports / Retry",
        "0. Exit"
    };

    public int SelectedMenuIndex { get; set; } = 0;
    public int SelectedView { get; set; }
    public int Page { get; set; }
    public bool IsTesting { get; set; }
    public SavedReportStore Store { get; } = new();
    public List<SavedReport> SavedReports { get; set; } = new();
    public string StorageMessage { get; set; } = "";
    public void LoadSavedReports()
    {
        // Keep unsaved in-memory reports when a disk write failed.
        var memory = SavedReports.ToArray();
        SavedReports = Store.Load();
        foreach (var report in memory)
        {
            SavedReports.RemoveAll(r => r.ReportId == report.ReportId);
            SavedReports.Add(report);
        }
        SavedReports = SavedReports.OrderByDescending(r => r.SavedAtUtc).ToList();
        StorageMessage = Store.IsPersistent ? "USB report storage: persistent" :
            OperatingSystem.IsLinux() ? "Local reports: live USB reboot loses data without PRAMAAN_DATA" : "Local report storage";
        if (Store.LoadErrors.Count > 0) StorageMessage += " | Unreadable files: see Messages";
    }
    public SystemInfo? SystemInfo { get; set; }
    public int DeviceId { get; set; } = 0;
    public string? HealthId { get; set; }
    public QCReport? Report { get; set; }

    // Scores
    public int OverallScore { get; set; } = 0;
    public string GradeLabel { get; set; } = "N/A";
    public int WarningCount { get; set; } = 0;
    public int FailCount { get; set; } = 0;

    // Progress
    public int ProgressCpu { get; set; } = 0;
    public int ProgressRam { get; set; } = 0;
    public int ProgressStorage { get; set; } = 0;
    public int ProgressGpu { get; set; } = 0;
    public int ProgressComp { get; set; } = 0;
    public string Elapsed { get; set; } = "00:00:00";
    public string StatusMessage { get; set; } = "Ready. Use Up/Down and Enter to navigate.";
    public string FooterMessage { get; set; } = "";

    public List<RecentReportEntry> RecentReports { get; set; } = new();

    private string GetRecentReportsPath()
    {
        var appDataDir = LaptopQC.Core.Models.AppPaths.AppDataDir;
        Directory.CreateDirectory(appDataDir);
        return Path.Combine(appDataDir, "recent_reports.json");
    }

    public void LoadRecentReports()
    {
        try
        {
            var path = GetRecentReportsPath();
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<List<RecentReportEntry>>(json);
                if (loaded != null)
                {
                    RecentReports = loaded;
                }
            }
        }
        catch { /* Ignore errors on load */ }
    }

    public void SaveRecentReports()
    {
        try
        {
            var path = GetRecentReportsPath();
            // Keep the last 10 reports
            var toSave = RecentReports.Take(10).ToList();
            var json = JsonSerializer.Serialize(toSave);
            File.WriteAllText(path, json);
        }
        catch { /* Ignore errors on save */ }
    }

    public void UpdateFromReport(QCReport r)
    {
        Report = r;
        HealthId = r.HealthId; // Update health ID from report
        OverallScore = r.PramaanResult?.OverallHealthScore ?? r.OverallScore;
        GradeLabel = GetGradeLabel(r.PramaanResult?.GradeBand ?? r.OverallGrade);

        // Count failures and warnings
        var tests = new[] { r.CpuTest, r.RamTest, r.StorageTest, r.BatteryTest,
                            r.GpuTest, r.NetworkTest, r.KeyboardTest, r.TrackpadTest,
                            r.UsbTest, r.AudioVideoTest, r.AudioJackTest };
        FailCount = tests.Count(t => t.Tested && !t.Passed);
        WarningCount = 0; // Could add warning logic based on scores < threshold
    }

    private static string GetGradeLabel(string? grade) => grade switch
    {
        "A+" => "EXCELLENT",
        "A"  => "VERY GOOD",
        "B"  => "GOOD",
        "C"  => "FAIR",
        "D"  => "POOR",
        "E"  => "VERY POOR",
        "Reject" => "REJECTED",
        "F"  => "FAILED",
        _    => "N/A"
    };
}
