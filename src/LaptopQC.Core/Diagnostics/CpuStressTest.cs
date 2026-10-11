#if WINDOWS
using LaptopQC.Core.Abstractions;
using LaptopQC.Hardware.Providers;
using System.Diagnostics;
using System.Management;

namespace LaptopQC.Core.Diagnostics;

/// <summary>
/// CPU stress test with reliable thermal monitoring using dedicated threads
/// </summary>
public class CpuStressTest : ICpuStressTest
{
    private readonly int _durationSeconds;
    private readonly int _threadCount;
    private volatile bool _isRunning;
    private readonly object _dataLock = new();

    // Shared data
    private readonly List<double> _temps = new();
    private readonly List<double> _clocks = new();
    
    // Throttle detector for advanced analysis
    private ThermalThrottleDetector? _throttleDetector;

    public event Action<StressTestProgress>? OnProgress;

    private readonly ISensorProvider? _sensorProvider;

    public CpuStressTest(int durationSeconds = 30, int? threadCount = null, ISensorProvider? sensorProvider = null)
    {
        _durationSeconds = durationSeconds;
        // Use slightly fewer threads to leave room for monitoring
        _threadCount = threadCount ?? Math.Max(1, Environment.ProcessorCount - 1);
        _sensorProvider = sensorProvider;
    }

    public async Task<CpuStressResult> RunAsync(CancellationToken cancellationToken = default)
    {
        // Run slow initialization on background thread to prevent UI freeze
        int baseClockMHz = 0;
        ISensorProvider? sensors = null;
        
        await Task.Run(() =>
        {
            // Get base clock from WMI for throttle detection
            baseClockMHz = GetBaseClockFromWmi();
            
            // Initialize sensors (this takes time due to WMI/LibreHardwareMonitor)
            sensors = _sensorProvider ?? new SensorProvider();
            if (_sensorProvider == null) sensors.Initialize();
        }, cancellationToken);
        
        _throttleDetector = new ThermalThrottleDetector(baseClockMHz);
        
        var result = new CpuStressResult
        {
            ThreadsUsed = _threadCount,
            DurationSeconds = _durationSeconds,
            StartTime = DateTime.Now,
            BaseClockMHz = baseClockMHz
        };

        _isRunning = true;
        _temps.Clear();
        _clocks.Clear();

        var stopwatch = Stopwatch.StartNew();
        var stressThreads = new List<Thread>();

        // 1. Start dedicated monitoring thread (HIGH PRIORITY)
        var monitorThread = new Thread(() => MonitorLoop(stopwatch, cancellationToken, sensors))
        {
            Priority = ThreadPriority.AboveNormal,
            IsBackground = true,
            Name = "ThermalMonitor"
        };
        monitorThread.Start();

        // Small delay to ensure monitor is running first
        await Task.Delay(100, cancellationToken);

        // 2. Start stress threads (NORMAL PRIORITY)
        for (int i = 0; i < _threadCount; i++)
        {
            var t = new Thread(() => StressLoop(cancellationToken))
            {
                Priority = ThreadPriority.Normal,
                IsBackground = true,
                Name = $"StressWorker{i}"
            };
            stressThreads.Add(t);
            t.Start();
        }

        // 3. Wait for test duration
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_durationSeconds), cancellationToken);
        }
        catch (TaskCanceledException) { }

        // 4. Stop everything. Thread.Join can block for up to a second per
        // worker. Do the shutdown off the caller's synchronization context;
        // RunAsync is normally awaited by the Avalonia UI thread.
        await Task.Run(() =>
        {
            _isRunning = false;

            foreach (var t in stressThreads)
                t.Join(1000);
            monitorThread.Join(1000);
        });

        stopwatch.Stop();

        // 5. Analyze results
        lock (_dataLock)
        {
            result.EndTime = DateTime.Now;
            result.ActualDurationSeconds = stopwatch.Elapsed.TotalSeconds;
            result.MaxTemp = _temps.Count > 0 ? _temps.Max() : 0;
            result.AvgTemp = _temps.Count > 0 ? _temps.Average() : 0;
            result.MinClock = _clocks.Count > 0 ? _clocks.Min() : 0;
            result.MaxClock = _clocks.Count > 0 ? _clocks.Max() : 0;
            result.AvgClock = _clocks.Count > 0 ? _clocks.Average() : 0;
        }

        // 6. Advanced throttle analysis
        var throttleAnalysis = _throttleDetector?.Analyze();
        result.ThrottleAnalysis = throttleAnalysis;
        
        // Determine pass/fail based on throttle analysis
        if (throttleAnalysis != null)
        {
            result.Passed = throttleAnalysis.Verdict == ThrottleVerdict.Excellent || 
                           throttleAnalysis.Verdict == ThrottleVerdict.Pass;
            result.Message = throttleAnalysis.Message;
            
            // Add pattern details if any concerning patterns detected
            var concerningPatterns = throttleAnalysis.Patterns
                .Where(p => p.Severity >= ThrottleSeverity.Moderate)
                .ToList();
            
            if (concerningPatterns.Any())
            {
                result.Message += " | Patterns: " + string.Join("; ", 
                    concerningPatterns.Select(p => p.Description));
            }
        }
        else
        {
            // Fallback to legacy detection if throttle detector failed
            var failures = new List<string>();
            
            if (result.MaxTemp > 95)
                failures.Add($"CRITICAL: CPU overheated ({result.MaxTemp:F1}°C > 95°C)");
            else if (result.MaxTemp > 90)
                failures.Add($"WARNING: High temperature ({result.MaxTemp:F1}°C)");

            if (result.MaxClock > 0 && result.MaxTemp > 80)
            {
                double dropPercent = (1 - (result.MinClock / result.MaxClock)) * 100;
                if (dropPercent > 25)
                    failures.Add($"THROTTLING: Speed dropped {dropPercent:F0}%");
            }

            // Test completion check
            if (stopwatch.Elapsed.TotalSeconds < _durationSeconds * 0.9 && !cancellationToken.IsCancellationRequested)
                failures.Add("Test interrupted unexpectedly");

            result.Passed = failures.Count == 0;
            result.Message = result.Passed
                ? $"PASSED: Max {result.MaxTemp:F1}°C, Speed stable at ~{result.AvgClock:F0}MHz"
                : $"FAILED: {string.Join("; ", failures)}";
        }

        return result;
    }

    private void MonitorLoop(Stopwatch stopwatch, CancellationToken ct, ISensorProvider sensors)
    {
        var logPath = Path.Combine(LaptopQC.Core.Models.AppPaths.AppDataDir, "stress_debug.log");
        
        try
        {
            using var log = new StreamWriter(logPath, append: false);
            log.WriteLine("Time,Temp(C),Clock(MHz),MaxClock,Throttle%");
            log.WriteLine("DEBUG: MonitorLoop started (sensors pre-initialized)");
            log.Flush();

            while (_isRunning && !ct.IsCancellationRequested)
            {
                try
                {
                    sensors.Update();
                    var temp = sensors.GetCpuTemperature();
                    var clock = sensors.GetCpuClockSpeed();

                    lock (_dataLock)
                    {
                        if (temp.HasValue) _temps.Add(temp.Value);
                        if (clock.HasValue) _clocks.Add(clock.Value);
                        
                        // Feed data to throttle detector
                        if (temp.HasValue && clock.HasValue)
                            _throttleDetector?.RecordSample(temp.Value, clock.Value);

                        double maxClock = _clocks.Count > 0 ? _clocks.Max() : 0;
                        double throttle = maxClock > 0 ? (1 - (clock ?? maxClock) / maxClock) * 100 : 0;

                        log.WriteLine($"{stopwatch.Elapsed.TotalSeconds:F1},{temp:F1},{clock:F0},{maxClock:F0},{throttle:F1}");
                        log.Flush();

                        OnProgress?.Invoke(new StressTestProgress
                        {
                            ElapsedSeconds = (int)stopwatch.Elapsed.TotalSeconds,
                            TotalSeconds = _durationSeconds,
                            PercentComplete = Math.Min(100, (int)(stopwatch.Elapsed.TotalSeconds / _durationSeconds * 100)),
                            CurrentTemp = temp ?? 0,
                            CurrentClock = clock ?? 0
                        });
                    }

                    Thread.Sleep(1000);
                }
                catch (Exception ex)
                {
                    log.WriteLine($"LOOP ERROR: {ex.Message}");
                    log.Flush();
                }
            }
            
            log.WriteLine("DEBUG: MonitorLoop exiting");
            if (_sensorProvider == null) sensors?.Dispose();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(LaptopQC.Core.Models.AppPaths.AppDataDir, "monitor_crash.log"), ex.ToString());
        }
    }

    private void StressLoop(CancellationToken ct)
    {
        double x = 1.0;
        while (_isRunning && !ct.IsCancellationRequested)
        {
            // Heavy FP math to stress CPU
            for (int i = 0; i < 500000; i++)
            {
                x = Math.Sqrt(x * x + Math.Sin(x) * Math.Cos(x) + 1.0);
            }
        }
    }
    
    /// <summary>
    /// Gets the base clock speed from WMI (Win32_Processor.MaxClockSpeed)
    /// Note: MaxClockSpeed in WMI is actually the base clock, not boost clock
    /// </summary>
    private static int GetBaseClockFromWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT MaxClockSpeed FROM Win32_Processor");
            foreach (ManagementObject obj in searcher.Get())
            {
                var speed = obj["MaxClockSpeed"];
                if (speed != null)
                    return Convert.ToInt32(speed);
            }
        }
        catch
        {
            // Fallback if WMI fails
        }
        return 2000; // Default fallback: 2 GHz
    }
}

public class CpuStressResult
{
    public bool Passed { get; set; }
    public string Message { get; set; } = "";
    public int ThreadsUsed { get; set; }
    public int DurationSeconds { get; set; }
    public double ActualDurationSeconds { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public double MaxTemp { get; set; }
    public double AvgTemp { get; set; }
    public double MinClock { get; set; }
    public double MaxClock { get; set; }
    public double AvgClock { get; set; }
    
    // New: Base clock and advanced throttle analysis
    public int BaseClockMHz { get; set; }
    public ThrottleAnalysisResult? ThrottleAnalysis { get; set; }
    
    /// <summary>
    /// Get a detailed summary of the stress test results
    /// </summary>
    public string GetDetailedSummary()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== CPU Stress Test Results ===");
        sb.AppendLine($"Verdict: {(Passed ? "PASS" : "FAIL")} - {Message}");
        sb.AppendLine($"Duration: {ActualDurationSeconds:F1}s with {ThreadsUsed} threads");
        sb.AppendLine($"Base Clock: {BaseClockMHz} MHz");
        sb.AppendLine($"Temperature: {AvgTemp:F0}°C avg, {MaxTemp:F0}°C max");
        sb.AppendLine($"Clock Speed: {AvgClock:F0} MHz avg ({MinClock:F0} - {MaxClock:F0} MHz)");
        
        if (ThrottleAnalysis != null)
        {
            sb.AppendLine();
            sb.AppendLine($"--- Throttle Analysis ---");
            sb.AppendLine($"Verdict: {ThrottleAnalysis.Verdict}");
            sb.AppendLine($"Sustained: {ThrottleAnalysis.AvgPercentOfBase:F0}% of base clock");
            sb.AppendLine($"Minimum: {ThrottleAnalysis.MinPercentOfBase:F0}% of base clock");
            sb.AppendLine($"Stability: {ThrottleAnalysis.ClockStabilityPercent:F0}%");
            
            if (ThrottleAnalysis.Patterns.Any())
            {
                sb.AppendLine("Detected Patterns:");
                foreach (var p in ThrottleAnalysis.Patterns)
                    sb.AppendLine($"  [{p.Severity}] {p.Description}");
            }
        }
        
        return sb.ToString();
    }
}

public class StressTestProgress
{
    public int ElapsedSeconds { get; set; }
    public int TotalSeconds { get; set; }
    public int PercentComplete { get; set; }
    public double CurrentTemp { get; set; }
    public double CurrentClock { get; set; }
}
#endif
