using System.Diagnostics;
using System.Text.RegularExpressions;
using LaptopQC.Core.Diagnostics;

namespace Pramaan.CLI.Diagnostics;

// ─────────────────────────────────────────────────────────────────
// Linux CPU Stress Test
//
// Mirrors CpuStressTest.cs (Windows) in structure:
//   • Spawns (ProcessorCount - 1) stress threads running heavy FP math
//   • A dedicated monitor thread reads temperature + clock speed every second
//   • After the duration, analyses for thermal throttling
//   • Returns a CpuStressResult (the #if !WINDOWS model from DiagnosticModels.cs)
//
// Temperature sources (tried in order):
//   1. /sys/class/thermal/thermal_zone*/temp   (most common, always present)
//   2. `sensors` command output (lm-sensors)
//
// Clock speed sources (tried in order):
//   1. /sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq
//   2. /proc/cpuinfo "cpu MHz" field
// ─────────────────────────────────────────────────────────────────

public class LinuxCpuStressTest
{
    private readonly int _durationSeconds;
    private readonly int _threadCount;
    private volatile bool _isRunning;
    private readonly object _dataLock = new();

    private readonly List<double> _temps  = new();
    private readonly List<double> _clocks = new();
    
    private ThermalThrottleDetector? _throttleDetector;

    public event Action<StressTestProgress>? OnProgress;

    public LinuxCpuStressTest(int durationSeconds = 15, int? threadCount = null)
    {
        _durationSeconds = durationSeconds;
        _threadCount     = threadCount ?? Math.Max(1, Environment.ProcessorCount - 1);
    }

    public async Task<CpuStressResult> RunAsync(CancellationToken ct = default)
    {
        int baseClockMHz = GetBaseClockFromLinux();
        _throttleDetector = new ThermalThrottleDetector(baseClockMHz);

        _isRunning = true;
        _temps.Clear();
        _clocks.Clear();

        var stopwatch = Stopwatch.StartNew();

        // ── Start monitor thread ──────────────────────────────────
        var monitorThread = new Thread(() => MonitorLoop(stopwatch, ct))
        {
            Priority     = ThreadPriority.AboveNormal,
            IsBackground = true,
            Name         = "LinuxThermalMonitor"
        };
        monitorThread.Start();
        await Task.Delay(100, ct).ConfigureAwait(false); // let monitor warm up

        // ── Start stress threads ──────────────────────────────────
        var stressThreads = new List<Thread>();
        for (int i = 0; i < _threadCount; i++)
        {
            var t = new Thread(() => StressLoop(ct))
            {
                Priority     = ThreadPriority.Normal,
                IsBackground = true,
                Name         = $"StressWorker{i}"
            };
            stressThreads.Add(t);
            t.Start();
        }

        // ── Wait for test duration ────────────────────────────────
        try { await Task.Delay(TimeSpan.FromSeconds(_durationSeconds), ct); }
        catch (TaskCanceledException) { }

        _isRunning = false;
        foreach (var t in stressThreads) t.Join(1000);
        monitorThread.Join(1000);
        stopwatch.Stop();

        // ── Analyse results ───────────────────────────────────────
        double maxTemp, avgTemp, minClock, maxClock, avgClock;
        lock (_dataLock)
        {
            maxTemp  = _temps.Count  > 0 ? _temps.Max()     : 0;
            avgTemp  = _temps.Count  > 0 ? _temps.Average()  : 0;
            minClock = _clocks.Count > 0 ? _clocks.Min()    : 0;
            maxClock = _clocks.Count > 0 ? _clocks.Max()    : 0;
            avgClock = _clocks.Count > 0 ? _clocks.Average() : 0;
        }

        bool passed;
        string message;
        bool thermalThrottle = false;

        // ── Advanced throttle analysis ────────────────────────────
        var throttleAnalysis = _throttleDetector?.Analyze();
        
        if (throttleAnalysis != null && throttleAnalysis.Verdict != ThrottleVerdict.InsufficientData)
        {
            passed = throttleAnalysis.Verdict == ThrottleVerdict.Excellent || 
                     throttleAnalysis.Verdict == ThrottleVerdict.Pass ||
                     (maxTemp == 0); // Allow pass if no temps
                     
            message = throttleAnalysis.Message;
            if (maxTemp == 0) message = "PASS: Stress completed (temperature sensors not available)";
            
            thermalThrottle = throttleAnalysis.Verdict == ThrottleVerdict.Warning || 
                              throttleAnalysis.Verdict == ThrottleVerdict.Fail || 
                              throttleAnalysis.Verdict == ThrottleVerdict.CriticalFail;
                              
            var concerningPatterns = throttleAnalysis.Patterns
                .Where(p => p.Severity >= ThrottleSeverity.Moderate)
                .ToList();
            
            if (concerningPatterns.Any())
            {
                message += " | Patterns: " + string.Join("; ", concerningPatterns.Select(p => p.Description));
            }
        }
        else
        {
            // ── Legacy Fallback ───────────────────────────────────
            if (maxTemp > 95)
            {
                passed         = false;
                thermalThrottle = true;
                message        = $"CRITICAL: CPU overheated ({maxTemp:F1}°C > 95°C threshold)";
            }
            else if (maxTemp > 90)
            {
                passed         = false;
                thermalThrottle = true;
                message        = $"FAIL: High temperature ({maxTemp:F1}°C) — cooling issue suspected";
            }
            else if (maxClock > 0 && minClock > 0 && maxTemp > 80)
            {
                double dropPct = (1.0 - minClock / maxClock) * 100.0;
                if (dropPct > 25)
                {
                    passed         = false;
                    thermalThrottle = true;
                    message        = $"FAIL: Thermal throttle detected — clock dropped {dropPct:F0}% under load";
                }
                else if (dropPct > 10)
                {
                    passed  = true;
                    message = $"WARNING: Minor throttle ({dropPct:F0}% clock drop, {maxTemp:F0}°C max)";
                }
                else
                {
                    passed  = true;
                    message = $"PASS: Stable at ~{avgClock:F0} MHz, {maxTemp:F0}°C max — EXCELLENT";
                }
            }
            else if (maxTemp == 0)
            {
                passed  = true;
                message = "PASS: Stress completed (temperature sensors not available)";
            }
            else
            {
                passed  = true;
                message = $"PASS: {maxTemp:F0}°C max, ~{avgClock:F0} MHz — EXCELLENT";
            }
        }

        return new CpuStressResult
        {
            Passed           = passed,
            Message          = message,
            MaxTemperature   = maxTemp > 0 ? maxTemp : null,
            ThermalThrottle  = thermalThrottle
        };
    }

    // ── Stress loop (FP-heavy, same as Windows version) ──────────
    private void StressLoop(CancellationToken ct)
    {
        double x = 1.0;
        while (_isRunning && !ct.IsCancellationRequested)
        {
            for (int i = 0; i < 500_000; i++)
                x = Math.Sqrt(x * x + Math.Sin(x) * Math.Cos(x) + 1.0);
        }
    }

    // ── Monitor loop ─────────────────────────────────────────────
    private void MonitorLoop(Stopwatch sw, CancellationToken ct)
    {
        while (_isRunning && !ct.IsCancellationRequested)
        {
            try
            {
                double? temp  = ReadCpuTemperature();
                double? clock = ReadCpuClockMHz();

                int elapsed  = (int)sw.Elapsed.TotalSeconds;
                int pct      = Math.Min(100, (int)(elapsed * 100.0 / _durationSeconds));

                lock (_dataLock)
                {
                    if (temp.HasValue)  _temps.Add(temp.Value);
                    if (clock.HasValue) _clocks.Add(clock.Value);
                    
                    if (temp.HasValue && clock.HasValue)
                        _throttleDetector?.RecordSample(temp.Value, clock.Value);
                }

                OnProgress?.Invoke(new StressTestProgress
                {
                    PercentComplete = pct,
                    Status          = temp.HasValue
                        ? $"Stress {pct}% — {temp.Value:F0}°C / {clock?.ToString("F0") ?? "??"} MHz"
                        : $"Stress {pct}% — running",
                    Temperature     = temp,
                    ClockSpeed      = clock
                });
            }
            catch { /* Never crash monitor */ }

            Thread.Sleep(1000);
        }
    }

    // ── Temperature reading ──────────────────────────────────────

    /// <summary>
    /// Reads CPU package temperature from Linux sysfs or lm-sensors.
    /// Returns null if not available (e.g. WSL).
    /// </summary>
    private static double? ReadCpuTemperature()
    {
        // 1. /sys/class/thermal/thermal_zone* — works on ARM + x86 without extras
        try
        {
            var thermalBase = "/sys/class/thermal";
            if (Directory.Exists(thermalBase))
            {
                var zones = Directory.GetDirectories(thermalBase, "thermal_zone*");
                var cpuTemps = new List<double>();

                foreach (var zone in zones)
                {
                    // Filter to CPU zones only (type = "x86_pkg_temp", "cpu-thermal", "acpitz", etc.)
                    var zoneType = LinuxCommandRunner.ReadFile(Path.Combine(zone, "type")).ToLowerInvariant();
                    if (zoneType.Contains("cpu")  || zoneType.Contains("pkg")  ||
                        zoneType.Contains("acpi") || zoneType.Contains("soc"))
                    {
                        var tempStr = LinuxCommandRunner.ReadFile(Path.Combine(zone, "temp"));
                        if (long.TryParse(tempStr, out long tempMilliC) && tempMilliC > 0)
                        {
                            double tempC = tempMilliC / 1000.0;
                            if (tempC > 10 && tempC < 120) // sanity check
                                cpuTemps.Add(tempC);
                        }
                    }
                }

                if (cpuTemps.Count > 0)
                    return cpuTemps.Max();
            }
        }
        catch { }

        // 2. `sensors` output (lm-sensors package)
        try
        {
            var sensorsOut = LinuxCommandRunner.TryRun("sensors", "", 3000);
            // Look for "Package id 0" or "Tdie", "Tctl"
            foreach (var pattern in new[] { @"Package\s+id\s+0:\s+\+?([\d.]+)°C",
                                            @"Tdie:\s+\+?([\d.]+)°C",
                                            @"Tctl:\s+\+?([\d.]+)°C",
                                            @"CPU Temperature:\s+\+?([\d.]+)°C" })
            {
                var m = Regex.Match(sensorsOut, pattern, RegexOptions.IgnoreCase);
                if (m.Success && double.TryParse(m.Groups[1].Value, out double t))
                    return t;
            }
        }
        catch { }

        return null;
    }

    // ── Clock speed reading ──────────────────────────────────────

    private static double? ReadCpuClockMHz()
    {
        // 1. /sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq (in kHz)
        try
        {
            var freqStr = LinuxCommandRunner.ReadFile(
                "/sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq");
            if (long.TryParse(freqStr, out long khz) && khz > 0)
                return khz / 1000.0;
        }
        catch { }

        // 2. /proc/cpuinfo — average across all cores
        try
        {
            var cpuinfo = LinuxCommandRunner.ReadFile("/proc/cpuinfo");
            var matches = Regex.Matches(cpuinfo, @"^cpu MHz\s*:\s*([\d.]+)", RegexOptions.Multiline);
            if (matches.Count > 0)
            {
                double sum = 0;
                foreach (Match m in matches)
                    if (double.TryParse(m.Groups[1].Value, out double mhz))
                return sum / matches.Count;
            }
        }
        catch { }

        return null;
    }
    
    // ── Base Clock Helper ────────────────────────────────────────
    
    private static int GetBaseClockFromLinux()
    {
        // 1. Try lscpu model string (e.g. "Intel Core i7-8550U CPU @ 1.80GHz")
        try
        {
            var lscpu = LinuxCommandRunner.TryRun("lscpu", "");
            var m = Regex.Match(lscpu, @"Model name:.*?@\s*([\d.]+)\s*GHz", RegexOptions.IgnoreCase);
            if (m.Success && double.TryParse(m.Groups[1].Value, out double ghz))
                return (int)Math.Round(ghz * 1000);
                
            // Fallback: look for "CPU max MHz:" in lscpu and divide by an average boost ratio (approx 1.3x)
            var maxFreqMatch = Regex.Match(lscpu, @"CPU max MHz:\s+([\d.]+)");
            if (maxFreqMatch.Success && double.TryParse(maxFreqMatch.Groups[1].Value, out double maxMhz))
            {
                // Note: We divide by 1.3 as a rough heuristic to get "base" from "max boost"
                // since the thermal throttle detector evaluates drops relative to base clock.
                return (int)Math.Round(maxMhz / 1.3);
            }
        }
        catch { }

        return 2000; // Default fallback: 2 GHz
    }
}

// ── Result / Progress models ─────────────────────────────────────

public class CpuStressResult
{
    public bool    Passed           { get; set; }
    public string  Message          { get; set; } = "";
    public double? MaxTemperature   { get; set; }
    public bool    ThermalThrottle  { get; set; }
}

public class StressTestProgress
{
    public int     PercentComplete { get; set; }
    public string  Status         { get; set; } = "";
    public double? Temperature    { get; set; }
    public double? ClockSpeed     { get; set; }
}
