using LaptopQC.Core.Models;
using System.Text.RegularExpressions;

namespace LaptopQC.Core.Services;

/// <summary>
/// Device grade tiers for refurbished laptop quality assessment
/// </summary>
public enum DeviceGrade
{
    A,  // 90-100: Like-new condition
    B,  // 80-89:  Good condition
    C,  // 70-79:  Acceptable condition
    D,  // 50-69:  Below average
    E,  // 1-49:   Poor condition
    F   // 0:      Non-functional
}

/// <summary>
/// Defines a scorable test component for the grading system.
/// ──────────────────────────────────────────────────────────
/// To ADD a new test:    Add a TestDefinition to GradingService.TestDefinitions
/// To REMOVE a test:     Remove its entry (or set Weight = 0)
/// To CHANGE priority:   Adjust the Weight value
/// ──────────────────────────────────────────────────────────
/// </summary>
public class TestDefinition
{
    /// <summary>Identifier matching the test type</summary>
    public required string Name { get; init; }
    
    /// <summary>Weight in the overall score. Higher = more impact on grade.</summary>
    public required int Weight { get; init; }
    
    /// <summary>Extracts the TestResult from a QCReport</summary>
    public required Func<QCReport, TestResult> GetResult { get; init; }
    
    /// <summary>
    /// Scoring function: takes (QCReport, TestResult) and returns 0-100.
    /// Return null to exclude this test from the weighted average
    /// (e.g., no battery on desktop, no discrete GPU).
    /// </summary>
    public required Func<QCReport, TestResult, int?> ScoreFunc { get; init; }
}

/// <summary>
/// Grades refurbished devices using a weighted scoring system.
/// All test definitions live in TestDefinitions — just add/remove entries.
/// </summary>
public class GradingService
{
    // ═══════════════════════════════════════════════════════════
    //  TEST REGISTRY — Add, remove, or reorder tests here
    // ═══════════════════════════════════════════════════════════

    public static readonly List<TestDefinition> TestDefinitions = new()
    {
        new() { Name = "Battery",    Weight = 25, GetResult = r => r.BatteryTest,    ScoreFunc = ScoreBattery },
        new() { Name = "SMART",      Weight = 0,  GetResult = r => r.SmartTest,      ScoreFunc = (_, _) => null }, // Deprecated, merged into Storage
        new() { Name = "CPU",        Weight = 15, GetResult = r => r.CpuTest,        ScoreFunc = ScoreCpu },
        new() { Name = "GPU",        Weight = 10, GetResult = r => r.GpuTest,        ScoreFunc = ScoreGpu },
        new() { Name = "Keyboard",   Weight = 10, GetResult = r => r.KeyboardTest,   ScoreFunc = ScoreBinary },
        new() { Name = "RAM",        Weight = 5,  GetResult = r => r.RamTest,        ScoreFunc = ScoreRam },
        new() { Name = "Trackpad",   Weight = 5,  GetResult = r => r.TrackpadTest,   ScoreFunc = ScoreBinary },
        new() { Name = "Display",    Weight = 5,  GetResult = r => r.DisplayTest,    ScoreFunc = ScoreBinary },
        new() { Name = "Storage",    Weight = 24, GetResult = r => r.StorageTest,    ScoreFunc = ScoreStorage },
        new() { Name = "USB",        Weight = 4,  GetResult = r => r.UsbTest,        ScoreFunc = ScoreBinary },
        new() { Name = "AudioVideo", Weight = 4,  GetResult = r => r.AudioVideoTest, ScoreFunc = ScoreBinary },
        new() { Name = "AudioJack",  Weight = 2,  GetResult = r => r.AudioJackTest,  ScoreFunc = ScoreBinary },
        new() { Name = "Network",    Weight = 3,  GetResult = r => r.NetworkTest,    ScoreFunc = ScoreNetwork },
    };

    // ═══════════════════════════════════════════════════════════
    //  PUBLIC API
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Scores and grades every test, then calculates the overall device grade.
    /// Call after ALL tests (automated + interactive) are complete.
    /// Also runs the PRAMAAN scoring engine (category-based, configurable).
    /// </summary>
    public void GradeReport(QCReport report, PramaanScoringConfig? pramaanConfig = null)
    {
        foreach (var def in TestDefinitions)
        {
            var testResult = def.GetResult(report);

            if (!testResult.Tested)
                continue;

            int? score = def.ScoreFunc(report, testResult);

            if (score == null)
                continue;

            int clamped = Math.Clamp(score.Value, 0, 100);
            testResult.Score = clamped;
            testResult.Grade = ScoreToGrade(clamped).ToString();
        }

        // PRAMAAN has entirely replaced the legacy overall score logic
        report.OverallScore = 0; 
        report.OverallGrade = "PRAMAAN";

        // ── PRAMAAN scoring ──
        var pramaanEngine = new PramaanScoringEngine(pramaanConfig);
        report.PramaanResult = pramaanEngine.ScoreReport(report);
    }

    /// <summary>
    /// Scores and grades only selected components. Does NOT compute overall grade or PRAMAAN.
    /// Returns a dictionary of component grades keyed by lowercase component name.
    /// </summary>
    public Dictionary<string, ComponentGrade> GradeComponentTestsOnly(QCReport report, IEnumerable<string> componentNames)
    {
        var selected = new HashSet<string>(componentNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, ComponentGrade>(StringComparer.OrdinalIgnoreCase);

        foreach (var def in TestDefinitions)
        {
            if (!selected.Contains(def.Name))
                continue;

            var testResult = def.GetResult(report);
            if (!testResult.Tested)
                continue;

            int? score = def.ScoreFunc(report, testResult);
            if (score == null)
                continue;

            int clamped = Math.Clamp(score.Value, 0, 100);
            testResult.Score = clamped;
            testResult.Grade = ScoreToGrade(clamped).ToString();

            var key = def.Name.ToLowerInvariant();
            result[key] = new ComponentGrade
            {
                Score = clamped,
                Grade = testResult.Grade
            };
        }

        return result;
    }

    /// <summary>Maps a score (0-100) to a DeviceGrade</summary>
    public static DeviceGrade ScoreToGrade(int score) => score switch
    {
        >= 90 => DeviceGrade.A,
        >= 80 => DeviceGrade.B,
        >= 70 => DeviceGrade.C,
        >= 50 => DeviceGrade.D,
        > 0   => DeviceGrade.E,
        _     => DeviceGrade.F
    };

    /// <summary>Human-readable label for a grade letter</summary>
    public static string GradeLabel(string grade) => grade switch
    {
        "A" => "Like-New Condition",
        "B" => "Good Condition",
        "C" => "Acceptable Condition",
        "D" => "Below Average",
        "E" => "Poor Condition",
        "F" => "Non-Functional",
        _   => "Unknown"
    };

    // ═══════════════════════════════════════════════════════════
    //  SCORING FUNCTIONS (0-100, null = exclude)
    // ═══════════════════════════════════════════════════════════

    /// <summary>Battery: health % + cycle count penalty. Null for desktops.</summary>
    private static int? ScoreBattery(QCReport report, TestResult _)
    {
        if (report.BatteryDetails == null || !report.BatteryDetails.IsPresent)
            return null;

        // Hard penalty when BMS data is invalid/unreadable (tamper indication).
        if (report.BatteryDetails.IsTampered)
            return 0;

        int health = report.BatteryDetails.HealthPercent ?? 100;
        int score = health switch
        {
            >= 90 => 100,
            >= 80 => 85,
            >= 70 => 70,
            >= 60 => 55,
            >= 50 => 40,
            _     => 20
        };

        int? cycles = report.BatteryDetails.CycleCount;
        if (cycles.HasValue)
        {
            if (cycles.Value > 1500) score -= 10;
            else if (cycles.Value > 1000) score -= 5;
        }
        else
        {
            // Missing cycle count: apply a small neutral penalty.
            score -= 3;
        }

        return Math.Max(0, score);
    }



    /// <summary>CPU: scored by throttle verdict keywords in test message.</summary>
    private static int? ScoreCpu(QCReport report, TestResult result)
    {
        if (!result.Tested) return null;

        string msg = (result.Message ?? "").ToUpperInvariant();

        if (msg.Contains("EXCELLENT")) return 100;
        if (msg.Contains("CRITICAL"))  return 10;
        if (msg.Contains("FAIL"))      return 30;
        if (msg.Contains("WARNING"))   return 55;

        // If stress pass details include almost no clock drop, treat as top-tier CPU behavior.
        var smallestDrop = result.Details
            .Select(d => Regex.Match(d, @"\((\d+)% drop\)"))
            .Where(m => m.Success)
            .Select(m => int.TryParse(m.Groups[1].Value, out int drop) ? drop : 100)
            .DefaultIfEmpty(100)
            .Min();

        if (result.Passed && smallestDrop <= 5) return 100;
        if (msg.Contains("PASS"))      return 90;

        return result.Passed ? 90 : 25;
    }

    /// <summary>GPU: scored by temp/throttle. Null if no discrete GPU.</summary>
    private static int? ScoreGpu(QCReport report, TestResult result)
    {
        if (!result.Tested) return null;

        string msg = result.Message ?? "";
        if (msg.Contains("No discrete GPU", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!result.Passed) return 15;

        // Parse max temp and clock drop from details
        double maxTemp = 0, clockDrop = 0;
        foreach (var detail in result.Details)
        {
            var tempMatch = Regex.Match(detail, @"Max Temperature:\s*([\d.]+)");
            if (tempMatch.Success) double.TryParse(tempMatch.Groups[1].Value, out maxTemp);

            var dropMatch = Regex.Match(detail, @"\((\d+)% drop\)");
            if (dropMatch.Success) double.TryParse(dropMatch.Groups[1].Value, out clockDrop);
        }

        int score = maxTemp switch
        {
            > 95 => 15,
            > 90 => 55,
            > 80 => 80,
            _    => 100
        };

        if (clockDrop > 30) score -= 25;

        return Math.Clamp(score, 0, 100);
    }

    /// <summary>RAM: stress pass/fail.</summary>
    private static int? ScoreRam(QCReport report, TestResult result)
    {
        if (!result.Tested) return null;

        if (result.Details.Any(d => d.Contains("Stress Test Failed", StringComparison.OrdinalIgnoreCase)))
            return 20;

        return result.Passed ? 100 : 25;
    }

    /// <summary>
    /// Storage scoring is built bottom-up:
    /// 1. Score each drive from health telemetry first.
    /// 2. Weight drives by capacity to get one machine-level storage score.
    /// 3. Apply trust penalties for self-test failures, suspicious data, and
    ///    other verification issues.
    /// This keeps SMART health central instead of letting a clean pass
    /// automatically inflate the score to 100.
    /// </summary>
    private static int? ScoreStorage(QCReport report, TestResult result)
    {
        if (!result.Tested) return null;

        if (report.StorageDetails?.IsTampered == true)
            return 0;

        if (report.StorageDetails?.IsInconclusive == true)
        {
            // Inconclusive is forgivable on healthy RAID machines that lack SMART passthrough
            if (report.StorageDetails.RaidArrays.Count > 0 &&
                report.StorageDetails.RaidArrays.All(a => a.IsHealthy))
                return 85; // RAID healthy but no individual SMART data
            return 35;
        }

        int score = ScoreStorageFromDrives(report) ?? ScoreStorageFromRaid(report) ?? (result.Passed ? 70 : 25);

        bool hasSelfTestFailure = result.Details.Any(d =>
            d.Contains("Self-Test Failed", StringComparison.OrdinalIgnoreCase) &&
            !d.Contains("Skipped", StringComparison.OrdinalIgnoreCase));
        bool hasSelfTestInconclusive = result.Details.Any(d =>
            d.Contains("Self-Test Inconclusive", StringComparison.OrdinalIgnoreCase));

        // RAID self-test skips are intentional and must not penalize the score
        bool hasSelfTestSkippedRaid = result.Details.Any(d =>
            d.Contains("Self-Test Skipped", StringComparison.OrdinalIgnoreCase) &&
            d.Contains("RAID", StringComparison.OrdinalIgnoreCase));

        if (hasSelfTestFailure)
        {
            score -= 30;
            score = Math.Min(score, 45);
        }
        else if (hasSelfTestInconclusive && !hasSelfTestSkippedRaid)
        {
            score -= 10;
        }

        if (!result.Passed)
            score = Math.Min(score, 45);

        if (report.StorageDetails?.IsSuspicious == true)
            score -= 15;

        // If the primary/only drive is eMMC and lacks definitive health telemetry,
        // keep it passable but never premium.
        if (report.StorageDetails != null && report.StorageDetails.Devices.Any() &&
            report.StorageDetails.Devices.All(d => d.IsEMMC && !d.HealthPercent.HasValue))
        {
            score = Math.Min(score, 80);
        }

        return Math.Clamp(score, 0, 100);
    }

    /// <summary>
    /// Scores RAID arrays when no per-drive SMART data is available.
    /// Healthy arrays → 90 (can't be 100 without individual SMART),
    /// any degraded → 20, elevated disk errors → penalty.
    /// </summary>
    private static int? ScoreStorageFromRaid(QCReport report)
    {
        if (report.StorageDetails == null || report.StorageDetails.RaidArrays.Count == 0)
            return null;

        bool allHealthy = report.StorageDetails.RaidArrays.All(a => a.IsHealthy);
        int base_ = allHealthy ? 90 : 20;

        // Penalise for elevated disk errors surfaced by the event log scan
        int errCount = report.StorageDetails.RaidDiskErrorEventCount;
        int errPenalty = errCount switch
        {
            0       => 0,
            <= 5    => 5,
            <= 20   => 15,
            _       => 25
        };

        return Math.Clamp(base_ - errPenalty, 0, 100);
    }

    private static int? ScoreStorageFromDrives(QCReport report)
    {
        if (report.StorageDetails == null || report.StorageDetails.Devices.Count == 0)
            return null;

        double weightedTotal = 0;
        double totalWeight = 0;

        foreach (var drive in report.StorageDetails.Devices)
        {
            // Skip RAID virtual disks — they have no SMART health% and are scored
            // separately by ScoreStorageFromRaid using array-level health.
            if (drive.IsRaid) continue;

            int driveScore = ScoreSingleDrive(drive);
            double weight = drive.SizeGB > 0 ? drive.SizeGB : 1;

            weightedTotal += driveScore * weight;
            totalWeight += weight;
        }

        if (totalWeight <= 0)
            return null;

        return (int)Math.Round(weightedTotal / totalWeight);
    }

    private static int ScoreSingleDrive(LaptopQC.Core.Diagnostics.StorageDevice drive)
    {
        int score;

        if (drive.HealthPercent.HasValue)
        {
            // SMART health is the primary signal.
            score = drive.HealthPercent.Value;
        }
        else if (drive.IsEMMC)
        {
            // eMMC often lacks reliable SMART-style wear telemetry.
            score = 80;
        }
        else if (drive.Temperature.HasValue || drive.PowerOnHours.HasValue || drive.TotalBytesWritten.HasValue)
        {
            // Some telemetry exists, but not enough to trust it fully.
            score = 65;
        }
        else
        {
            score = 55;
        }

        if (drive.Temperature.HasValue)
        {
            score -= drive.Temperature.Value switch
            {
                <= 45 => 0,
                <= 50 => 4,
                <= 55 => 10,
                <= 60 => 20,
                _     => 35
            };
        }

        if (drive.IsSuspicious)
            score -= 10;

        return Math.Clamp(score, 0, 100);
    }

    /// <summary>Binary scoring for interactive tests: 100 if passed, 0 if failed.</summary>
    private static int? ScoreBinary(QCReport _, TestResult result)
    {
        if (!result.Tested) return null;
        return result.Passed ? 100 : 0;
    }

    /// <summary>
    /// Network scoring: WiFi+Ethernet = 100, WiFi only = 85, Ethernet only = 70, none = 0.
    /// Looks for "WiFi: Connected" / "Ethernet: Connected" in result.Details.
    /// </summary>
    private static int? ScoreNetwork(QCReport _, TestResult result)
    {
        if (!result.Tested) return null;
        if (!result.Passed) return 0;

        bool hasWifi = result.Details.Any(d => d.Contains("WiFi: Connected", StringComparison.OrdinalIgnoreCase));
        bool hasEthernet = result.Details.Any(d => d.Contains("Ethernet: Connected", StringComparison.OrdinalIgnoreCase));

        return (hasWifi, hasEthernet) switch
        {
            (true, true)   => 100,
            (true, false)  => 85,
            (false, true)  => 70,
            _              => result.Passed ? 80 : 0  // Fallback if details don't match
        };
    }
}
