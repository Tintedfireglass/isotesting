namespace Pramaan.CLI.Diagnostics;

// ─────────────────────────────────────────────────────────────────
// Linux RAM Stress Test
//
// Mirrors RamStressTest.cs (Windows) exactly:
//   • Allocates a configurable block (default 512 MB)
//   • Fills with a repeating pattern, then reads back to verify
//   • Runs for the requested number of iterations (default 2)
//   • Any mismatch → failure
// ─────────────────────────────────────────────────────────────────

public class LinuxRamStressTest
{
    private readonly int _testSizeMB;
    private readonly int _iterations;

    public event Action<RamStressProgress>? OnProgress;

    public LinuxRamStressTest(int testSizeMB = 512, int iterations = 2)
    {
        _testSizeMB = testSizeMB;
        _iterations = iterations;
    }

    public async Task<RamStressTestResult> RunAsync(CancellationToken ct = default)
    {
        return await Task.Run(() => RunInternal(ct), ct);
    }

    private RamStressTestResult RunInternal(CancellationToken ct)
    {
        long blockBytes = (long)_testSizeMB * 1024 * 1024;
        int blockInts   = (int)(blockBytes / 4); // work in 32-bit words

        try
        {
            var buffer = new int[blockInts];

            for (int iter = 0; iter < _iterations; iter++)
            {
                if (ct.IsCancellationRequested)
                    return new RamStressTestResult { Passed = false, Message = "Cancelled" };

                // Fill pass — use a deterministic pattern so we can verify
                int seed = 0x5A5A5A5A ^ iter;
                for (int i = 0; i < blockInts; i++)
                    buffer[i] = seed ^ i;

                Report(iter, 50); // halfway

                // Verify pass
                for (int i = 0; i < blockInts; i++)
                {
                    int expected = seed ^ i;
                    if (buffer[i] != expected)
                    {
                        return new RamStressTestResult
                        {
                            Passed  = false,
                            Message = $"Stress Test Failed: Memory error at offset {i * 4} (iter {iter + 1})"
                        };
                    }
                }

                Report(iter, 100);
            }

            // GC before returning so we don't leave a large allocation pinned
            buffer = null!;
            GC.Collect();

            return new RamStressTestResult
            {
                Passed  = true,
                Message = "Stress Test Passed"
            };
        }
        catch (OutOfMemoryException)
        {
            return new RamStressTestResult
            {
                Passed  = false,
                Message = $"Stress Test Failed: Insufficient memory to allocate {_testSizeMB} MB test block"
            };
        }
        catch (Exception ex)
        {
            return new RamStressTestResult
            {
                Passed  = false,
                Message = $"Stress Test Failed: {ex.Message}"
            };
        }
    }

    private void Report(int iteration, int phasePercent)
    {
        int overallPct = (int)(((iteration * 100.0) + phasePercent) / _iterations);
        OnProgress?.Invoke(new RamStressProgress
        {
            PercentComplete = Math.Min(100, overallPct),
            CurrentIteration = iteration + 1,
            TotalIterations  = _iterations
        });
    }
}

public class RamStressTestResult
{
    public bool   Passed  { get; set; }
    public string Message { get; set; } = "";
}

public class RamStressProgress
{
    public int PercentComplete   { get; set; }
    public int CurrentIteration  { get; set; }
    public int TotalIterations   { get; set; }
}
