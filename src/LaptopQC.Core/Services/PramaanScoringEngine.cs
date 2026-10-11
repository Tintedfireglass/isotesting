using LaptopQC.Core.Models;
using LaptopQC.Core.Diagnostics;
using System.Text.RegularExpressions;

namespace LaptopQC.Core.Services;

/// <summary>
/// PRAMAAN Scoring Engine v1.
/// 
/// Aggregates individual test results into 6 weighted categories,
/// normalizes each to 0–100, and produces an overall health score,
/// grade band, and risk flags.
///
/// All configuration is externalized via PramaanScoringConfig.
/// Nothing is hardcoded — weights, thresholds, and grade bands
/// are all swappable at runtime.
/// </summary>
public class PramaanScoringEngine
{
    private readonly PramaanScoringConfig _config;

    public PramaanScoringEngine(PramaanScoringConfig? config = null)
    {
        _config = config ?? new PramaanScoringConfig();
    }

    /// <summary>
    /// Creates a scoring engine initialized with the live configuration from the web API.
    /// Falls back to defaults if the API is unreachable.
    /// </summary>
    public static async Task<PramaanScoringEngine> CreateAsync()
    {
        var configService = new PramaanConfigService();
        var liveConfig = await configService.GetActiveConfigAsync();
        return new PramaanScoringEngine(liveConfig);
    }

    /// <summary>
    /// Score a QCReport and produce a full PramaanResult.
    /// </summary>
    public PramaanResult ScoreReport(QCReport report)
    {
        var categoryScores = new Dictionary<string, int>();

        // ── Category: Storage (SMART + Storage diagnostics) ──
        categoryScores["storage"] = ScoreStorage(report);

        // ── Category: Thermal (CPU thermal + GPU thermal) ──
        categoryScores["thermal"] = ScoreThermal(report);

        // ── Category: Battery ──
        categoryScores["battery"] = ScoreBattery(report);

        // ── Category: CPU + RAM ──
        categoryScores["cpu_ram"] = ScoreCpuRam(report);

        // ── Category: Physical Ports ──
        categoryScores["physical_ports"] = ScorePhysicalPorts(report);

        // ── Category: Repair Modifier ──
        categoryScores["repair_modifier"] = ScoreRepairModifier(report);

        // ── Weighted aggregation ──
        double totalWeightedScore = 0;
        double totalWeight = 0;

        foreach (var kvp in _config.Weights)
        {
            if (categoryScores.TryGetValue(kvp.Key, out int catScore))
            {
                totalWeightedScore += catScore * kvp.Value;
                totalWeight += kvp.Value;
            }
        }

        int overallScore = totalWeight > 0
            ? (int)Math.Round(totalWeightedScore / totalWeight)
            : 0;

        overallScore = Math.Clamp(overallScore, 0, 100);

        // ── Risk flags ──
        var riskFlags = new Dictionary<string, bool>();
        foreach (var kvp in _config.RiskThresholds)
        {
            if (categoryScores.TryGetValue(kvp.Key, out int catScore))
                riskFlags[kvp.Key] = catScore < kvp.Value;
            else
                riskFlags[kvp.Key] = false;
        }

        return new PramaanResult
        {
            OverallHealthScore = overallScore,
            GradeBand = _config.ScoreToGrade(overallScore),
            CategoryScores = categoryScores,
            RiskFlags = riskFlags,
            AlgorithmVersion = $"Scoring Engine v{_config.Version}",
            GeneratedAt = DateTime.UtcNow
        };
    }

    // ═══════════════════════════════════════════════════════════
    //  CATEGORY SCORING — each returns normalized 0–100
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Storage: combines SMART health % per device, temperature, and self-test result.
    /// Health % is read directly from StorageDevice.HealthPercent (populated by smartctl).
    /// </summary>
    private int ScoreStorage(QCReport report)
    {
        if (report.StorageDetails?.IsTampered == true)
            return 0;

        var scores = new List<int>();

        // Storage health from detailed info
        if (report.StorageDetails != null)
        {
            foreach (var device in report.StorageDetails.Devices)
            {
                if (device.HealthPercent.HasValue)
                {
                    // Already 0–100, direct use
                    scores.Add(device.HealthPercent.Value);
                }

                // Temperature penalty: >55°C starts degrading score
                if (device.Temperature.HasValue)
                {
                    int tempScore = device.Temperature.Value switch
                    {
                        <= 40 => 100,
                        <= 50 => 85,
                        <= 55 => 70,
                        <= 60 => 50,
                        _     => 25
                    };
                    scores.Add(tempScore);
                }
            }
        }

        // Binary storage test fallback
        if (StorageTestPassed(report))
            scores.Add(100);
        else if (report.StorageTest.Tested)
            scores.Add(0);

        int finalScore = scores.Count > 0 ? (int)scores.Average() : 50; // neutral if untested

        if (report.StorageDetails?.IsSuspicious == true)
            finalScore -= 20;

        // Self-test failure is a hard penalty
        if (report.StorageTest.Details.Any(d => d.Contains("Self-Test Failed", StringComparison.OrdinalIgnoreCase) && !d.Contains("Skipped")))
        {
            finalScore = Math.Min(finalScore, 30);
        }

        // eMMC capping: untested long-term health
        if (report.StorageDetails != null && report.StorageDetails.Devices.Any() &&
            report.StorageDetails.Devices.All(d => d.IsEMMC && !d.HealthPercent.HasValue))
        {
            finalScore = Math.Min(finalScore, 80);
        }

        return Math.Clamp(finalScore, 0, 100);
    }

    /// <summary>
    /// Thermal: CPU thermal throttle severity + GPU thermal.
    /// Throttling verdict → severity score mapping.
    /// </summary>
    private int ScoreThermal(QCReport report)
    {
        var scores = new List<int>();

        // CPU thermal (from stress test verdict)
        if (report.CpuTest.Tested)
        {
            string msg = (report.CpuTest.Message ?? "").ToUpperInvariant();

            int cpuThermalScore;
            if (msg.Contains("EXCELLENT"))      cpuThermalScore = 100;
            else if (msg.Contains("CRITICAL"))  cpuThermalScore = 5;
            else if (msg.Contains("FAIL"))      cpuThermalScore = 20;
            else if (msg.Contains("WARNING"))   cpuThermalScore = 50;
            else if (msg.Contains("PASS"))      cpuThermalScore = 100;
            else                                cpuThermalScore = report.CpuTest.Passed ? 100 : 20;

            scores.Add(cpuThermalScore);
        }

        // GPU thermal
        if (report.GpuTest.Tested)
        {
            string gpuMsg = report.GpuTest.Message ?? "";

            // Skip "No discrete GPU" — not a risk indicator
            if (!gpuMsg.Contains("No discrete GPU", StringComparison.OrdinalIgnoreCase))
            {
                if (!report.GpuTest.Passed)
                {
                    scores.Add(10);
                }
                else
                {
                    // Parse max temp from GPU details
                    double maxTemp = 0;
                    foreach (var detail in report.GpuTest.Details)
                    {
                        var tempMatch = Regex.Match(detail, @"Max Temperature:\s*([\d.]+)");
                        if (tempMatch.Success) double.TryParse(tempMatch.Groups[1].Value, out maxTemp);
                    }

                    int gpuScore = maxTemp switch
                    {
                        > 95 => 10,
                        > 90 => 45,
                        > 85 => 65,
                        > 80 => 80,
                        _    => 100
                    };
                    scores.Add(gpuScore);
                }
            }
        }

        return scores.Count > 0 ? (int)scores.Average() : 70; // neutral default
    }

    /// <summary>
    /// Battery: health %, cycle count, wear level.
    /// Already normalized — health % is direct.
    /// </summary>
    private int ScoreBattery(QCReport report)
    {
        if (report.BatteryDetails == null || !report.BatteryDetails.IsPresent)
            return 100; // No battery = desktop, no penalty

        // Tampered/unreadable BMS data: treat as a hard failure + strong penalty.
        if (report.BatteryDetails.IsTampered)
            return 0;

        int health = report.BatteryDetails.HealthPercent ?? 100;

        // Map health % to score with non-linear curve
        int score = health switch
        {
            >= 90 => 100,
            >= 80 => 88,
            >= 70 => 72,
            >= 60 => 55,
            >= 50 => 38,
            >= 40 => 22,
            _     => 10
        };

        // Cycle count penalty
        int? cycles = report.BatteryDetails.CycleCount;
        if (cycles.HasValue)
        {
            if (cycles.Value > 1500)      score -= 15;
            else if (cycles.Value > 1000) score -= 8;
            else if (cycles.Value > 500)  score -= 3;
        }
        else
        {
            // Missing cycle count: apply a small neutral penalty.
            score -= 3;
        }

        // Wear level cross-check
        if (report.BatteryDetails.WearLevelPercent.HasValue)
        {
            int wear = report.BatteryDetails.WearLevelPercent.Value;
            if (wear > 40) score -= 10;
            else if (wear > 25) score -= 5;
        }

        return Math.Clamp(score, 0, 100);
    }

    /// <summary>
    /// CPU + RAM: stress test stability.
    /// CPU stress pass/fail + RAM stress result.
    /// </summary>
    private int ScoreCpuRam(QCReport report)
    {
        var scores = new List<int>();

        // CPU stress
        if (report.CpuTest.Tested)
        {
            scores.Add(report.CpuTest.Passed ? 100 : 25);
        }

        // RAM stress
        if (report.RamTest.Tested)
        {
            if (report.RamTest.Details.Any(d =>
                    d.Contains("Stress Test Failed", StringComparison.OrdinalIgnoreCase)))
            {
                scores.Add(15);
            }
            else
            {
                scores.Add(report.RamTest.Passed ? 100 : 20);
            }
        }

        return scores.Count > 0 ? (int)scores.Average() : 50;
    }

    /// <summary>
    /// Physical Ports: USB, Audio/Video, AudioJack, Keyboard, Trackpad, Network.
    /// Binary tests (pass/fail) normalized to 0 or 100, then averaged.
    /// </summary>
    private int ScorePhysicalPorts(QCReport report)
    {
        var scores = new List<int>();

        void AddBinary(TestResult test)
        {
            if (test.Tested)
                scores.Add(test.Passed ? 100 : 0);
        }

        AddBinary(report.UsbTest);
        AddBinary(report.AudioVideoTest);
        AddBinary(report.AudioJackTest);
        AddBinary(report.KeyboardTest);
        AddBinary(report.TrackpadTest);
        AddBinary(report.DisplayTest);

        // Network: graduated scoring
        if (report.NetworkTest.Tested)
        {
            if (!report.NetworkTest.Passed)
            {
                scores.Add(0);
            }
            else
            {
                bool hasWifi = report.NetworkTest.Details.Any(d =>
                    d.Contains("WiFi: Connected", StringComparison.OrdinalIgnoreCase));
                bool hasEthernet = report.NetworkTest.Details.Any(d =>
                    d.Contains("Ethernet: Connected", StringComparison.OrdinalIgnoreCase));

                int netScore = (hasWifi, hasEthernet) switch
                {
                    (true, true)   => 100,
                    (true, false)  => 85,
                    (false, true)  => 70,
                    _              => 80
                };
                scores.Add(netScore);
            }
        }

        return scores.Count > 0 ? (int)scores.Average() : 50;
    }

    /// <summary>
    /// Repair Modifier: placeholder for Saarthi integration.
    /// Uses config default when no repair data is available.
    /// </summary>
    private int ScoreRepairModifier(QCReport report)
    {
        // Future: read repair history from Saarthi via report.RepairHistory
        // For now, use the config default
        return _config.DefaultRepairModifierScore;
    }

    // ═══════════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════════

    /// <summary>Check if the storage binary test passed (helper to avoid confusion with SMART).</summary>
    private bool StorageTestPassed(QCReport report)
    {
        return report.StorageTest.Tested && report.StorageTest.Passed;
    }
}
