namespace LaptopQC.Core.Diagnostics;

/// <summary>
/// Generic thermal throttling detection algorithm that works across all systems.
/// Uses dynamic analysis of temperature and frequency patterns during stress testing.
/// </summary>
public class ThermalThrottleDetector
{
    // Configuration thresholds
    // Note: Laptops on battery deliberately run below base clock - these are relaxed
    private const double CRITICAL_TEMP_CELSIUS = 95.0;
    private const double HIGH_TEMP_CELSIUS = 85.0;
    private const double SAFE_TEMP_CEILING = 75.0;
    
    // Frequency thresholds (% of base clock)
    // More relaxed to account for battery mode and power-saving
    private const double CRITICAL_DROP_PERCENT = 50.0;  // Below 50% of base = critical  
    private const double SEVERE_DROP_PERCENT = 30.0;    // Below 70% of base = fail
    private const double MODERATE_DROP_PERCENT = 15.0;  // Below 85% of base = warning
    private const double MILD_DROP_PERCENT = 5.0;       // Below 95% of base = minor
    
    // Sampling window for pattern detection
    private const int MIN_SAMPLES_FOR_ANALYSIS = 5;
    private const int STABILITY_WINDOW_SECONDS = 10;
    
    // Rolling data storage
    private readonly List<ThermalSample> _samples = new();
    private readonly int _baseClockMHz;
    private double _observedMaxClock;
    private DateTime _stressStartTime;
    
    public ThermalThrottleDetector(int baseClockMHz)
    {
        _baseClockMHz = baseClockMHz > 0 ? baseClockMHz : 2000; // Fallback to 2GHz
        _observedMaxClock = baseClockMHz;
    }
    
    /// <summary>
    /// Record a sample during stress testing
    /// </summary>
    public void RecordSample(double temperatureCelsius, double clockSpeedMHz)
    {
        if (_samples.Count == 0)
            _stressStartTime = DateTime.Now;
            
        var sample = new ThermalSample
        {
            Timestamp = DateTime.Now,
            Temperature = temperatureCelsius,
            ClockSpeed = clockSpeedMHz,
            ElapsedSeconds = (DateTime.Now - _stressStartTime).TotalSeconds
        };
        
        _samples.Add(sample);
        
        // Track observed maximum (usually happens in first few seconds before thermal ramp)
        if (clockSpeedMHz > _observedMaxClock)
            _observedMaxClock = clockSpeedMHz;
    }
    
    /// <summary>
    /// Analyze all collected samples and produce a throttling verdict
    /// </summary>
    public ThrottleAnalysisResult Analyze()
    {
        if (_samples.Count < MIN_SAMPLES_FOR_ANALYSIS)
        {
            return new ThrottleAnalysisResult
            {
                Verdict = ThrottleVerdict.InsufficientData,
                Message = $"Need at least {MIN_SAMPLES_FOR_ANALYSIS} samples for analysis"
            };
        }
        
        var result = new ThrottleAnalysisResult
        {
            TotalSamples = _samples.Count,
            TestDurationSeconds = _samples.Last().ElapsedSeconds,
            BaseClockMHz = _baseClockMHz,
            ObservedMaxClockMHz = _observedMaxClock
        };
        
        // Calculate statistics
        CalculateStatistics(result);
        
        // Detect throttling patterns
        DetectThrottlingPatterns(result);
        
        // Determine final verdict
        DetermineVerdict(result);
        
        return result;
    }
    
    private void CalculateStatistics(ThrottleAnalysisResult result)
    {
        var temps = _samples.Select(s => s.Temperature).ToList();
        var clocks = _samples.Select(s => s.ClockSpeed).ToList();
        
        result.MaxTemperature = temps.Max();
        result.AvgTemperature = temps.Average();
        result.MinTemperature = temps.Min();
        
        result.MaxClockMHz = clocks.Max();
        result.MinClockMHz = clocks.Min();
        result.AvgClockMHz = clocks.Average();
        
        // Key metric: percentage of base clock sustained
        result.MinPercentOfBase = (result.MinClockMHz / _baseClockMHz) * 100;
        result.AvgPercentOfBase = (result.AvgClockMHz / _baseClockMHz) * 100;
        
        // Calculate frequency stability (standard deviation)
        double avgClock = result.AvgClockMHz;
        result.ClockStabilityPercent = 100 - (clocks.Select(c => Math.Abs(c - avgClock)).Average() / avgClock * 100);
    }
    
    private void DetectThrottlingPatterns(ThrottleAnalysisResult result)
    {
        result.Patterns = new List<ThrottlePattern>();
        
        // Pattern 1: Immediate throttling (within first 5 seconds)
        var earlySamples = _samples.Where(s => s.ElapsedSeconds <= 5).ToList();
        var lateSamples = _samples.Where(s => s.ElapsedSeconds > 5).ToList();
        
        if (earlySamples.Any() && lateSamples.Any())
        {
            double earlyAvgClock = earlySamples.Average(s => s.ClockSpeed);
            double lateAvgClock = lateSamples.Average(s => s.ClockSpeed);
            double earlyAvgTemp = earlySamples.Average(s => s.Temperature);
            
            if (lateAvgClock < earlyAvgClock * 0.9)
            {
                if (earlyAvgTemp < HIGH_TEMP_CELSIUS)
                {
                    result.Patterns.Add(new ThrottlePattern
                    {
                        Type = ThrottlePatternType.ImmediateProactive,
                        Description = "CPU reduced speed immediately under load (proactive thermal management)",
                        Severity = ThrottleSeverity.Mild
                    });
                }
                else
                {
                    result.Patterns.Add(new ThrottlePattern
                    {
                        Type = ThrottlePatternType.ImmediateReactive,
                        Description = "CPU throttled immediately due to rapid heat buildup",
                        Severity = ThrottleSeverity.Severe
                    });
                }
            }
        }
        
        // Pattern 2: Progressive throttling (correlates with temperature rise)
        var tempClockCorrelation = CalculateCorrelation(
            _samples.Select(s => s.Temperature).ToList(),
            _samples.Select(s => s.ClockSpeed).ToList());
        
        if (tempClockCorrelation < -0.5) // Strong negative correlation
        {
            result.Patterns.Add(new ThrottlePattern
            {
                Type = ThrottlePatternType.TemperatureCorrelated,
                Description = $"Clock speed decreases as temperature rises (correlation: {tempClockCorrelation:F2})",
                Severity = result.MaxTemperature > HIGH_TEMP_CELSIUS ? ThrottleSeverity.Severe : ThrottleSeverity.Moderate
            });
        }
        
        // Pattern 3: Sudden drops (potential PROCHOT or power limit)
        for (int i = 1; i < _samples.Count; i++)
        {
            var prev = _samples[i - 1];
            var curr = _samples[i];
            
            double dropPercent = (prev.ClockSpeed - curr.ClockSpeed) / prev.ClockSpeed * 100;
            
            if (dropPercent > 20) // >20% sudden drop
            {
                result.Patterns.Add(new ThrottlePattern
                {
                    Type = curr.Temperature > 90 ? ThrottlePatternType.ThermalShutdown : ThrottlePatternType.PowerLimit,
                    Description = $"Sudden {dropPercent:F0}% frequency drop at {curr.ElapsedSeconds:F0}s (temp: {curr.Temperature:F0}°C)",
                    Severity = ThrottleSeverity.Severe,
                    OccurredAtSeconds = curr.ElapsedSeconds
                });
            }
        }
        
        // Pattern 4: Sustained low frequency (below 70% - only flag if significant)
        var sustainedLowSamples = _samples
            .Where(s => s.ClockSpeed < _baseClockMHz * 0.70 && s.ElapsedSeconds > 10)
            .ToList();
        
        if (sustainedLowSamples.Count > _samples.Count * 0.3) // >30% of test at low freq
        {
            double avgLowTemp = sustainedLowSamples.Average(s => s.Temperature);
            result.Patterns.Add(new ThrottlePattern
            {
                Type = ThrottlePatternType.SustainedThrottle,
                Description = $"CPU ran below 70% base clock for {sustainedLowSamples.Count * 100 / _samples.Count}% of test",
                Severity = avgLowTemp > HIGH_TEMP_CELSIUS ? ThrottleSeverity.Critical : ThrottleSeverity.Moderate
            });
        }
        
        // Pattern 5: Recovery check - did frequency recover when temp dropped?
        var peakTempIndex = _samples.FindIndex(s => s.Temperature == result.MaxTemperature);
        if (peakTempIndex >= 0 && peakTempIndex < _samples.Count - 3)
        {
            var afterPeakSamples = _samples.Skip(peakTempIndex + 1).ToList();
            if (afterPeakSamples.Any())
            {
                bool tempDropped = afterPeakSamples.Last().Temperature < result.MaxTemperature - 5;
                bool clockRecovered = afterPeakSamples.Last().ClockSpeed > _samples[peakTempIndex].ClockSpeed * 1.05;
                
                if (tempDropped && clockRecovered)
                {
                    result.Patterns.Add(new ThrottlePattern
                    {
                        Type = ThrottlePatternType.HealthyRecovery,
                        Description = "Frequency recovered when temperature decreased (thermal management working)",
                        Severity = ThrottleSeverity.None
                    });
                }
            }
        }
    }
    
    private void DetermineVerdict(ThrottleAnalysisResult result)
    {
        // Priority 1: Critical temperature breach
        if (result.MaxTemperature >= CRITICAL_TEMP_CELSIUS)
        {
            result.Verdict = ThrottleVerdict.CriticalFail;
            result.Message = $"CRITICAL: CPU reached {result.MaxTemperature:F0}°C (limit: {CRITICAL_TEMP_CELSIUS}°C)";
            return;
        }
        
        // Priority 2: Severe frequency drop below base clock
        if (result.MinPercentOfBase < (100 - CRITICAL_DROP_PERCENT))
        {
            result.Verdict = ThrottleVerdict.Fail;
            result.Message = $"FAIL: CPU dropped to {result.MinPercentOfBase:F0}% of base clock ({result.MinClockMHz:F0}/{_baseClockMHz} MHz)";
            return;
        }
        
        // Priority 3: Moderate throttling with high temperature
        if (result.MinPercentOfBase < (100 - SEVERE_DROP_PERCENT) && result.MaxTemperature > HIGH_TEMP_CELSIUS)
        {
            result.Verdict = ThrottleVerdict.Warning;
            result.Message = $"WARNING: Thermal throttling detected - {result.MinPercentOfBase:F0}% of base clock at {result.MaxTemperature:F0}°C";
            return;
        }
        
        // Priority 4: Mild throttling (normal for laptops)
        if (result.MinPercentOfBase < (100 - MILD_DROP_PERCENT))
        {
            // Check if sustained or brief
            bool wasSustained = result.Patterns.Any(p => p.Type == ThrottlePatternType.SustainedThrottle);
            
            if (wasSustained)
            {
                result.Verdict = ThrottleVerdict.Warning;
                result.Message = $"WARNING: Sustained throttling to {result.AvgPercentOfBase:F0}% of base clock";
            }
            else
            {
                result.Verdict = ThrottleVerdict.Pass;
                result.Message = $"PASS: Minor throttling ({result.MinPercentOfBase:F0}% min), max temp {result.MaxTemperature:F0}°C";
            }
            return;
        }
        
        // Priority 5: Excellent - no significant throttling
        result.Verdict = ThrottleVerdict.Excellent;
        result.Message = $"EXCELLENT: Sustained {result.AvgPercentOfBase:F0}% of base clock, max temp {result.MaxTemperature:F0}°C";
    }
    
    /// <summary>
    /// Pearson correlation coefficient between two data sets
    /// </summary>
    private static double CalculateCorrelation(List<double> x, List<double> y)
    {
        if (x.Count != y.Count || x.Count < 2)
            return 0;
            
        double avgX = x.Average();
        double avgY = y.Average();
        
        double sumNumerator = 0;
        double sumX2 = 0;
        double sumY2 = 0;
        
        for (int i = 0; i < x.Count; i++)
        {
            double dx = x[i] - avgX;
            double dy = y[i] - avgY;
            sumNumerator += dx * dy;
            sumX2 += dx * dx;
            sumY2 += dy * dy;
        }
        
        double denominator = Math.Sqrt(sumX2 * sumY2);
        return denominator == 0 ? 0 : sumNumerator / denominator;
    }
    
    /// <summary>
    /// Reset for a new test
    /// </summary>
    public void Reset()
    {
        _samples.Clear();
        _observedMaxClock = _baseClockMHz;
    }
}

#region Data Models

public class ThermalSample
{
    public DateTime Timestamp { get; set; }
    public double Temperature { get; set; }
    public double ClockSpeed { get; set; }
    public double ElapsedSeconds { get; set; }
}

public class ThrottleAnalysisResult
{
    public ThrottleVerdict Verdict { get; set; }
    public string Message { get; set; } = "";
    
    // Test info
    public int TotalSamples { get; set; }
    public double TestDurationSeconds { get; set; }
    public int BaseClockMHz { get; set; }
    public double ObservedMaxClockMHz { get; set; }
    
    // Temperature stats
    public double MaxTemperature { get; set; }
    public double AvgTemperature { get; set; }
    public double MinTemperature { get; set; }
    
    // Clock stats
    public double MaxClockMHz { get; set; }
    public double MinClockMHz { get; set; }
    public double AvgClockMHz { get; set; }
    
    // Key metrics
    public double MinPercentOfBase { get; set; }
    public double AvgPercentOfBase { get; set; }
    public double ClockStabilityPercent { get; set; }
    
    // Detected patterns
    public List<ThrottlePattern> Patterns { get; set; } = new();
    
    /// <summary>
    /// Get a summary for display
    /// </summary>
    public string GetSummary()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Verdict: {Verdict} - {Message}");
        sb.AppendLine($"Duration: {TestDurationSeconds:F0}s, Samples: {TotalSamples}");
        sb.AppendLine($"Temperature: {MinTemperature:F0}°C → {MaxTemperature:F0}°C (avg: {AvgTemperature:F0}°C)");
        sb.AppendLine($"Clock Speed: {MinClockMHz:F0} → {MaxClockMHz:F0} MHz (avg: {AvgClockMHz:F0} MHz)");
        sb.AppendLine($"Sustained: {AvgPercentOfBase:F0}% of base clock ({BaseClockMHz} MHz)");
        sb.AppendLine($"Stability: {ClockStabilityPercent:F0}%");
        
        if (Patterns.Any())
        {
            sb.AppendLine("\nDetected Patterns:");
            foreach (var pattern in Patterns.OrderByDescending(p => p.Severity))
            {
                sb.AppendLine($"  [{pattern.Severity}] {pattern.Description}");
            }
        }
        
        return sb.ToString();
    }
}

public class ThrottlePattern
{
    public ThrottlePatternType Type { get; set; }
    public string Description { get; set; } = "";
    public ThrottleSeverity Severity { get; set; }
    public double OccurredAtSeconds { get; set; }
}

public enum ThrottleVerdict
{
    InsufficientData,
    Excellent,      // No throttling, sustained boost
    Pass,           // Minor throttling, acceptable
    Warning,        // Noticeable throttling, borderline
    Fail,           // Significant throttling, cooling inadequate
    CriticalFail    // Critical temps or severe throttling
}

public enum ThrottlePatternType
{
    ImmediateProactive,     // CPU preemptively reduced speed
    ImmediateReactive,      // CPU throttled due to instant heat
    TemperatureCorrelated,  // Speed drops as temp rises
    PowerLimit,             // PL1/PL2 hit
    ThermalShutdown,        // Near critical temp
    SustainedThrottle,      // Long periods below base
    HealthyRecovery         // Speed recovered when cooled (good sign)
}

public enum ThrottleSeverity
{
    None,
    Mild,
    Moderate,
    Severe,
    Critical
}

#endregion
