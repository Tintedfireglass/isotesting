using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace LaptopQC.Core.Diagnostics;

/// <summary>
/// A CPU stress test for macOS that uses an iterations-per-second (IPS) drop approach
/// to detect thermal throttling, since reading exact CPU temperatures natively
/// requires root privileges and is not supported on Apple Silicon via standard tools.
/// </summary>
/// <remarks>
/// DEPRECATED — replaced by <see cref="MacThermalStressTest"/> which runs a proper
/// 4-phase protocol (BASELINE → CPU_RAMP → COMBINED_LOAD → COOLDOWN) with GIPS
/// measurement, concurrent RAM pressure, and a storage benchmark.
/// This class is retained for reference only and is no longer called from production code.
/// </remarks>
[Obsolete("Use MacThermalStressTest instead. This class will be removed in a future release.")]
public class MacCpuStressTest
{
    private readonly int _durationSeconds;
    public event Action<MacCpuStressProgress>? OnProgress;

    public MacCpuStressTest(int durationSeconds = 15)
    {

        _durationSeconds = durationSeconds;
    }

    public async Task<MacCpuStressResult> RunAsync()
    {
        int coreCount = Environment.ProcessorCount;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_durationSeconds));
        var token = cts.Token;

        long[] iterations = new long[coreCount];
        var tasks = new Task[coreCount];

        for (int i = 0; i < coreCount; i++)
        {
            int threadIndex = i;
            tasks[i] = Task.Run(() => WorkerThread(threadIndex, iterations, token), token);
        }

        var sw = Stopwatch.StartNew();
        long lastTotal = 0;
        double maxIps = 0;
        double minIps = double.MaxValue;
        
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(1000, CancellationToken.None);
            
            long currentTotal = 0;
            for (int i = 0; i < coreCount; i++)
                currentTotal += Interlocked.Read(ref iterations[i]);

            long diff = currentTotal - lastTotal;
            lastTotal = currentTotal;

            // Give it 2 seconds to warm up before measuring min/max to avoid initial spikes/dips
            if (sw.Elapsed.TotalSeconds > 2)
            {
                if (diff > maxIps) maxIps = diff;
                if (diff < minIps && diff > 0) minIps = diff;
            }

            int percent = (int)Math.Min(100, (sw.Elapsed.TotalSeconds / _durationSeconds) * 100);
            
            OnProgress?.Invoke(new MacCpuStressProgress
            {
                PercentComplete = percent,
                CurrentClock = (float)(diff / 1000000.0) // Map IPS to 'CurrentClock' for UI visualization
            });
            
            if (sw.Elapsed.TotalSeconds >= _durationSeconds)
                break;
        }

        cts.Cancel();
        try { await Task.WhenAll(tasks); } catch { }

        // Analyze throttling
        double throttlePercent = 0;
        if (maxIps > 0 && minIps < double.MaxValue && minIps <= maxIps)
        {
            throttlePercent = ((maxIps - minIps) / maxIps) * 100;
        }

        bool passed = throttlePercent < 25; // Less than 25% drop is considered a pass

        return new MacCpuStressResult
        {
            Passed = passed,
            Message = passed 
                ? $"CPU stress test passed (Peak: {maxIps / 1000000.0:F1}M ops/sec)" 
                : $"Thermal throttling detected ({throttlePercent:F1}% performance drop)",
            MaxClock = (float)(maxIps / 1000000.0),
            MinClock = (float)(minIps / 1000000.0)
        };
    }

    private void WorkerThread(int index, long[] iterations, CancellationToken token)
    {
        double x = 1.0;
        long count = 0;
        while (!token.IsCancellationRequested)
        {
            // Do some floating point math to heat up the CPU
            for (int i = 0; i < 10000; i++)
            {
                x = Math.Sqrt(x * x + Math.Sin(x) * Math.Cos(x) + 1.0);
            }
            count += 10000;
            
            if (count >= 100000)
            {
                Interlocked.Add(ref iterations[index], count);
                count = 0;
            }
        }
        Interlocked.Add(ref iterations[index], count);
    }
}

public class MacCpuStressProgress
{
    public int PercentComplete { get; set; }
    public float CurrentClock { get; set; }
}

public class MacCpuStressResult
{
    public bool Passed { get; set; }
    public string Message { get; set; } = "";
    public float MaxClock { get; set; }
    public float MinClock { get; set; }
}
