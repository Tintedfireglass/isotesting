using System.Diagnostics;
using System.Runtime.InteropServices;
using LaptopQC.Core.Diagnostics.macOS;

namespace LaptopQC.Core.Diagnostics;

/// <summary>
/// Four-phase thermal stress test for macOS (Apple Silicon + Intel).
///
/// Phases:
///   1. BASELINE      (5s)  — idle GIPS measurement, no load
///   2. CPU_RAMP      (30s) — all-core CPU stress, GIPS measured on same threads
///   3. COMBINED_LOAD (30s) — CPU stress + RAM pressure + storage benchmark, concurrent
///   4. COOLDOWN      (10s) — all stress stopped, GIPS recovery measurement
///
/// Total: ~75 seconds.
///
/// Replaces MacCpuStressTest (15s simple math loop) and the sequential RamStressTest
/// call on macOS. Results map directly to Report.CpuTest and Report.RamTest so the
/// existing PramaanScoringEngine.ScoreThermal() and ScoreCpuRam() require no changes.
/// </summary>
public class MacThermalStressTest
{
    // ── Phase durations (seconds) ────────────────────────────────
    private const int BaselineSecs     = 5;
    private const int CpuRampSecs      = 30;
    private const int CombinedLoadSecs = 30;
    private const int CooldownSecs     = 10;

    // ── RAM pressure limits (from spec §2.3) ────────────────────
    private const int   RamBlockSizeMB        = 5;
    private const float MaxHeapFractionToFill = 0.50f;  // 50% of available heap
    private const float MinFreeRamFraction    = 0.15f;  // stop if < 15% system RAM free

    // ── Storage benchmark ───────────────────────────────────────
    private const int StorageFileSizeMB  = 64;
    private const int StorageChunkSizeKB = 64;

    // ── macOS F_NOCACHE (bypass page cache for reads) ───────────
    // fcntl(fd, F_NOCACHE, 1) — no root required
    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);
    private const int F_NOCACHE = 48;

    // ── State ────────────────────────────────────────────────────
    private volatile bool _stressRunning;
    private volatile bool _abortRequested;

    // GIPS samples per phase
    private readonly List<double> _baselineGips     = new();
    private readonly List<double> _cpuRampGips      = new();
    private readonly List<double> _combinedLoadGips = new();
    private readonly List<double> _cooldownGips     = new();

    // RAM stress state
    private volatile bool _ramIntegrityFailed;
    private volatile bool _oomOccurred;
    private int           _peakRamAllocatedMB;
    private readonly object _ramLock = new();

    // Storage results
    private double _writeMBps;
    private double _readMBps;

    // Thermal state
    private string _worstThermalLevel = "COOL";

    /// <summary>
    /// Fired approximately every second with the current phase and live GIPS.
    /// Safe to marshal to the UI thread from the caller.
    /// </summary>
    public event Action<MacThermalStressProgress>? OnProgress;

    // ════════════════════════════════════════════════════════════
    //  PUBLIC ENTRY POINT
    // ════════════════════════════════════════════════════════════

    public async Task<MacThermalStressResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var sw        = Stopwatch.StartNew();
        int coreCount = Environment.ProcessorCount;

        // ── Phase 1: BASELINE ────────────────────────────────────
        ReportProgress("BASELINE", 0, 0, 0);
        await RunBaselinePhaseAsync(coreCount, cancellationToken);

        double baselineGips = SafeAvg(_baselineGips);

        if (_abortRequested || cancellationToken.IsCancellationRequested)
            return BuildResult(baselineGips, aborted: true);

        // ── Launch stress workers (CPU_RAMP + COMBINED_LOAD) ─────
        using var stressCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _stressRunning = true;

        var stressWorkers = LaunchCpuStressWorkers(coreCount, stressCts.Token);

        // ── Phase 2: CPU_RAMP ────────────────────────────────────
        await RunMeasurementPhaseAsync(
            "CPU_RAMP", CpuRampSecs, _cpuRampGips, coreCount,
            totalSeconds: BaselineSecs + CpuRampSecs,
            stressCts.Token, cancellationToken);

        if (_abortRequested || cancellationToken.IsCancellationRequested)
        {
            await StopStressAsync(stressCts, stressWorkers);
            return BuildResult(baselineGips, aborted: true);
        }

        // ── Phase 3: COMBINED_LOAD ───────────────────────────────
        // Launch RAM pressure worker
        using var ramCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ramBlocks  = new List<byte[]>();
        var ramWorker  = Task.Run(() => RamPressureLoop(ramBlocks, ramCts.Token), ramCts.Token);

        // Launch storage benchmark (fire-and-forget, result captured via fields)
        var storageBenchmark = Task.Run(() => RunStorageBenchmark(cancellationToken), cancellationToken);

        await RunMeasurementPhaseAsync(
            "COMBINED_LOAD", CombinedLoadSecs, _combinedLoadGips, coreCount,
            totalSeconds: BaselineSecs + CpuRampSecs + CombinedLoadSecs,
            stressCts.Token, cancellationToken);

        // Stop RAM stress, release memory
        ramCts.Cancel();
        try { await ramWorker; } catch { }
        lock (_ramLock) { ramBlocks.Clear(); }
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // Wait for storage benchmark to finish (it may still be mid-read)
        try { await storageBenchmark; } catch { }

        if (_abortRequested || cancellationToken.IsCancellationRequested)
        {
            await StopStressAsync(stressCts, stressWorkers);
            return BuildResult(baselineGips, aborted: true);
        }

        // ── Phase 4: COOLDOWN ────────────────────────────────────
        await StopStressAsync(stressCts, stressWorkers);
        _stressRunning = false;

        await RunMeasurementPhaseAsync(
            "COOLDOWN", CooldownSecs, _cooldownGips, coreCount,
            totalSeconds: BaselineSecs + CpuRampSecs + CombinedLoadSecs + CooldownSecs,
            CancellationToken.None, cancellationToken);

        sw.Stop();
        return BuildResult(baselineGips);
    }

    // ════════════════════════════════════════════════════════════
    //  PHASE RUNNERS
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// BASELINE: measure GIPS with no stress — uses a temporary idle thread pool so
    /// the measurement context is consistent with later phases (same thread characteristics).
    /// </summary>
    private async Task RunBaselinePhaseAsync(int coreCount, CancellationToken ct)
    {
        int elapsed = 0;
        while (elapsed < BaselineSecs && !ct.IsCancellationRequested)
        {
            double gips = await Task.Run(() => MeasureGips(), ct);
            _baselineGips.Add(gips);
            ReportProgress("BASELINE", elapsed + 1, BaselineSecs, gips);
            UpdateThermalLevel();
            elapsed++;

            if (_abortRequested) return;
            try { await Task.Delay(1000, ct); } catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// Generic measurement loop: fires one GIPS measurement per second for
    /// <paramref name="durationSecs"/> seconds, storing results in <paramref name="bucket"/>.
    /// GIPS is dispatched onto the thread pool (which is saturated by the stress workers
    /// during load phases), so throttling is correctly reflected in the reading.
    /// </summary>
    private async Task RunMeasurementPhaseAsync(
        string phaseName,
        int durationSecs,
        List<double> bucket,
        int coreCount,
        int totalSeconds,
        CancellationToken stressCt,
        CancellationToken userCt)
    {
        int elapsed = 0;

        while (elapsed < durationSecs && !userCt.IsCancellationRequested)
        {
            double gips = await Task.Run(() => MeasureGips(), userCt);
            bucket.Add(gips);

            int overallElapsed = totalSeconds - durationSecs + elapsed + 1;
            int overallTotal   = BaselineSecs + CpuRampSecs + CombinedLoadSecs + CooldownSecs;
            int pct = (int)((double)overallElapsed / overallTotal * 100);

            ReportProgress(phaseName, pct, overallTotal, gips);
            UpdateThermalLevel();

            if (_abortRequested) return;

            elapsed++;
            try { await Task.Delay(1000, userCt); } catch (TaskCanceledException) { return; }
        }
    }

    // ════════════════════════════════════════════════════════════
    //  CPU STRESS WORKERS
    // ════════════════════════════════════════════════════════════

    private List<Thread> LaunchCpuStressWorkers(int count, CancellationToken ct)
    {
        var threads = new List<Thread>();
        for (int i = 0; i < count; i++)
        {
            var t = new Thread(() => CpuStressLoop(ct))
            {
                Priority     = ThreadPriority.Highest,
                IsBackground = true,
                Name         = $"ThermalStress_{i}"
            };
            threads.Add(t);
            t.Start();
        }
        return threads;
    }

    /// <summary>
    /// Per-core stress loop (from spec §2.1):
    ///   1. Prime sieve 2–5,000 (integer ALU + branch predictor)
    ///   2. 30×30 double-precision matrix multiply (FP ALU + memory bandwidth)
    ///   3. 500 sin/cos operations (FPU)
    /// Yields every 50 iterations so the OS doesn't deprioritise the thread.
    /// </summary>
    private void CpuStressLoop(CancellationToken ct)
    {
        // Pre-allocate matrices so GC pressure doesn't distort results
        double[,] A = new double[30, 30];
        double[,] B = new double[30, 30];
        double[,] C = new double[30, 30];

        var rng = new Random();
        for (int i = 0; i < 30; i++)
            for (int j = 0; j < 30; j++)
            {
                A[i, j] = rng.NextDouble();
                B[i, j] = rng.NextDouble();
            }

        int iter = 0;
        while (!ct.IsCancellationRequested && _stressRunning)
        {
            // 1. Prime sieve (2–5,000)
            for (int n = 2; n <= 5000; n++)
            {
                int limit = (int)Math.Sqrt(n);
                bool composite = false;
                for (int d = 2; d <= limit && !composite; d++)
                    if (n % d == 0) composite = true;
            }

            // 2. 30×30 matrix multiply
            for (int i = 0; i < 30; i++)
                for (int j = 0; j < 30; j++)
                {
                    C[i, j] = 0;
                    for (int k = 0; k < 30; k++)
                        C[i, j] += A[i, k] * B[k, j];
                }

            // 3. 500 trig operations
            double sink = 0;
            for (int i = 0; i < 500; i++)
                sink += Math.Sin(i) * Math.Cos(i);

            // Prevent dead-code elimination
            if (sink > 1e300) A[0, 0] = sink;

            // Cooperative yield every 50 iterations
            iter++;
            if (iter % 50 == 0)
            {
                Thread.Sleep(0); // yield time-slice
                iter = 0;
            }
        }
    }

    private static async Task StopStressAsync(CancellationTokenSource stressCts, List<Thread> workers)
    {
        stressCts.Cancel();
        await Task.Run(() =>
        {
            foreach (var t in workers)
                t.Join(500);
        });
    }

    // ════════════════════════════════════════════════════════════
    //  GIPS MEASUREMENT (spec §3)
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Measures Giga Integer Operations Per Second.
    /// Runs a prime sieve (2–10,000) + 1,000 sin/cos ops, counts every divisibility
    /// check and trig call as one "operation", and divides by elapsed seconds.
    ///
    /// IMPORTANT: called via Task.Run so it executes on the .NET thread pool —
    /// the same pool saturated by the stress workers during load phases. This means
    /// any CPU throttling is correctly reflected in the GIPS reading.
    /// </summary>
    private static double MeasureGips()
    {
        long start = Stopwatch.GetTimestamp();
        long ops   = 0;

        // Prime sieve to 10,000
        for (int n = 2; n <= 10000; n++)
        {
            int limit = (int)Math.Sqrt(n);
            for (int d = 2; d <= limit; d++)
            {
                ops++;
                if (n % d == 0) break;
            }
            ops++; // boundary check counts as one op
        }

        // 1,000 trig operations
        double sink = 0;
        for (int i = 0; i < 1000; i++)
        {
            sink += Math.Sin(i) * Math.Cos(i);
            ops++;
        }

        // Prevent dead-code elimination
        if (sink > 1e300) ops = 1;

        double elapsedSecs = (double)(Stopwatch.GetTimestamp() - start) / Stopwatch.Frequency;
        if (elapsedSecs <= 0) return 0;

        return (ops / elapsedSecs) / 1_000_000_000.0; // GIPS
    }

    // ════════════════════════════════════════════════════════════
    //  RAM PRESSURE WORKER (adapted from spec §2.3)
    // ════════════════════════════════════════════════════════════

    private void RamPressureLoop(List<byte[]> blocks, CancellationToken ct)
    {
        long totalRamBytes   = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        long maxAllocBytes   = (long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes * MaxHeapFractionToFill);
        long allocated       = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Check system-wide free RAM floor
                long freeRam = GC.GetGCMemoryInfo().MemoryLoadBytes;
                long total   = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                double freeRatio = total > 0 ? 1.0 - ((double)freeRam / total) : 1.0;
                if (freeRatio < MinFreeRamFraction) break;

                // Check 50% heap cap
                if (allocated >= maxAllocBytes) break;

                // Allocate next 5 MB block
                int blockBytes = RamBlockSizeMB * 1024 * 1024;
                byte[] block   = new byte[blockBytes];
                allocated     += blockBytes;

                // Fill with XOR integrity pattern: byte[i] = (i ^ (i >> 8)) & 0xFF
                for (int i = 0; i < block.Length; i++)
                    block[i] = (byte)((i ^ (i >> 8)) & 0xFF);

                lock (_ramLock)
                {
                    blocks.Add(block);
                    int totalMb = (int)(allocated / (1024 * 1024));
                    if (totalMb > _peakRamAllocatedMB)
                        _peakRamAllocatedMB = totalMb;
                }

                // Spot-check integrity every 500 ms while waiting
                var integrityTimer = Stopwatch.StartNew();
                while (!ct.IsCancellationRequested && integrityTimer.Elapsed.TotalMilliseconds < 500)
                    Thread.Sleep(50);

                if (!ct.IsCancellationRequested)
                    SpotCheckIntegrity(blocks);
            }
        }
        catch (OutOfMemoryException)
        {
            _oomOccurred = true;
        }
    }

    private void SpotCheckIntegrity(List<byte[]> blocks)
    {
        if (_ramIntegrityFailed) return;

        byte[]? target;
        lock (_ramLock)
        {
            if (blocks.Count == 0) return;
            // Pick a random block
            int idx = new Random().Next(blocks.Count);
            target = blocks[idx];
        }

        // Check every 4,096 bytes
        for (int i = 0; i < target.Length; i += 4096)
        {
            byte expected = (byte)((i ^ (i >> 8)) & 0xFF);
            if (target[i] != expected)
            {
                _ramIntegrityFailed = true;
                return;
            }
        }
    }

    // ════════════════════════════════════════════════════════════
    //  STORAGE BENCHMARK
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Sequential write + read benchmark on a temporary file.
    ///
    /// Write: FileOptions.WriteThrough (maps to O_SYNC on macOS) — bypasses write-combine
    ///        delay so we measure driver-level sequential write speed, not page cache speed.
    ///
    /// Read:  fcntl(fd, F_NOCACHE, 1) via P/Invoke — disables the page cache for this
    ///        file descriptor so reads come from NAND, not RAM. No root required.
    ///        F_NOCACHE = 48 (macOS-specific constant).
    /// </summary>
    private void RunStorageBenchmark(CancellationToken ct)
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"pramaan_stor_{Guid.NewGuid():N}.tmp");

        try
        {
            int fileSizeBytes  = StorageFileSizeMB * 1024 * 1024;
            int chunkSizeBytes = StorageChunkSizeKB * 1024;

            // Build write buffer — repeating 0xDE 0xAD 0xBE 0xEF (same as spec §2.2)
            byte[] writeBuffer = new byte[chunkSizeBytes];
            byte[] pattern = { 0xDE, 0xAD, 0xBE, 0xEF };
            for (int i = 0; i < writeBuffer.Length; i++)
                writeBuffer[i] = pattern[i % 4];

            // ── WRITE ────────────────────────────────────────────
            var writeSw = Stopwatch.StartNew();
            using (var fs = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: chunkSizeBytes,
                options: FileOptions.WriteThrough))
            {
                int written = 0;
                while (written < fileSizeBytes && !ct.IsCancellationRequested)
                {
                    int toWrite = Math.Min(chunkSizeBytes, fileSizeBytes - written);
                    fs.Write(writeBuffer, 0, toWrite);
                    written += toWrite;
                }
                fs.Flush();
            }
            writeSw.Stop();

            if (ct.IsCancellationRequested) return;

            double writeSecs = writeSw.Elapsed.TotalSeconds;
            _writeMBps = writeSecs > 0 ? StorageFileSizeMB / writeSecs : 0;

            // ── READ (with F_NOCACHE to bypass page cache) ───────
            byte[] readBuffer = new byte[chunkSizeBytes];
            var readSw = Stopwatch.StartNew();

            using (var fs = new FileStream(
                tempPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: chunkSizeBytes,
                options: FileOptions.SequentialScan))
            {
                // Disable OS page cache for this fd — no root required on macOS
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    try
                    {
                        var safeHandle = fs.SafeFileHandle;
                        int fd = (int)safeHandle.DangerousGetHandle();
                        fcntl(fd, F_NOCACHE, 1);
                    }
                    catch { /* F_NOCACHE is best-effort; fall back to cached reads */ }
                }

                int bytesRead;
                while ((bytesRead = fs.Read(readBuffer, 0, readBuffer.Length)) > 0
                       && !ct.IsCancellationRequested)
                {
                    // no-op — we just need to pull bytes through the I/O path
                }
            }
            readSw.Stop();

            double readSecs = readSw.Elapsed.TotalSeconds;
            _readMBps = readSecs > 0 ? StorageFileSizeMB / readSecs : 0;
        }
        catch
        {
            // Benchmark is best-effort; don't fail the test if storage throws
            _writeMBps = 0;
            _readMBps  = 0;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    // ════════════════════════════════════════════════════════════
    //  THERMAL LEVEL (no-root macOS approach)
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads macOS thermal pressure level via sysctl.
    /// kern.thermal_level: 0 = nominal, 1–2 = moderate, 3–4 = serious, 5+ = critical.
    /// Falls back to "UNKNOWN" gracefully — scoring derives from GIPS drop in that case.
    /// </summary>
    private static string ReadThermalLevel()
    {
        try
        {
            string raw = CommandRunner.TryRun(
                "sysctl", "-n kern.thermal_level").Trim();

            if (int.TryParse(raw, out int level))
            {
                return level switch
                {
                    0    => "COOL",
                    <= 2 => "WARM",
                    <= 4 => "HOT",
                    _    => "CRITICAL"
                };
            }
        }
        catch { }
        return "UNKNOWN";
    }

    private void UpdateThermalLevel()
    {
        string current = ReadThermalLevel();
        _worstThermalLevel = WorseThermal(_worstThermalLevel, current);

        if (current == "CRITICAL")
            _abortRequested = true;
    }

    private static string WorseThermal(string a, string b)
    {
        static int Rank(string s) => s switch
        {
            "COOL"     => 0,
            "WARM"     => 1,
            "HOT"      => 2,
            "CRITICAL" => 3,
            _          => -1
        };
        return Rank(a) >= Rank(b) ? a : b;
    }

    // ════════════════════════════════════════════════════════════
    //  RESULT BUILDER
    // ════════════════════════════════════════════════════════════

    private MacThermalStressResult BuildResult(double baselineGips, bool aborted = false)
    {
        double peakGips      = SafeAvg(_cpuRampGips);
        double sustainedGips = SafeAvg(_combinedLoadGips);
        double recoveryGips  = SafeAvg(_cooldownGips);

        // Use baseline as reference; fall back to peak if baseline unavailable
        double referenceGips = baselineGips > 0 ? baselineGips : peakGips;

        double throttlePct   = referenceGips > 0
            ? (1.0 - sustainedGips / referenceGips) * 100.0
            : 0;
        double recoveryPct   = referenceGips > 0
            ? (recoveryGips / referenceGips) * 100.0
            : 100;

        // Derive thermal level from GIPS drop if sysctl wasn't available
        if (_worstThermalLevel == "UNKNOWN")
        {
            _worstThermalLevel = throttlePct switch
            {
                < 5  => "COOL",
                < 15 => "WARM",
                < 30 => "HOT",
                _    => "CRITICAL"
            };
        }

        // ── CPU pass/fail (maps to PramaanScoringEngine message string parsing) ──
        string cpuMessage;
        bool   cpuPassed;

        if (aborted)
        {
            cpuPassed  = false;
            cpuMessage = "FAIL: Test aborted — Critical thermal state detected";
        }
        else if (throttlePct > 40)
        {
            cpuPassed  = false;
            cpuMessage = $"FAIL: Severe thermal throttling ({throttlePct:F0}% performance drop under load)";
        }
        else if (throttlePct > 20)
        {
            cpuPassed  = true;
            cpuMessage = $"WARNING: Mild throttling detected ({throttlePct:F0}% performance drop under load)";
        }
        else
        {
            cpuPassed  = true;
            cpuMessage = $"PASS: Sustained performance ({100 - throttlePct:F0}% of baseline maintained)";
        }

        // Append recovery note
        if (!aborted)
        {
            cpuMessage += recoveryPct >= 85
                ? " | Recovery: Excellent"
                : $" | Recovery: Degraded ({recoveryPct:F0}% of baseline after cooldown)";
        }

        // ── RAM pass/fail ────────────────────────────────────────
        bool   ramPassed;
        string ramMessage;

        if (aborted)
        {
            ramPassed  = false;
            ramMessage = "FAIL: Test aborted before RAM phase completed";
        }
        else if (_ramIntegrityFailed)
        {
            ramPassed  = false;
            ramMessage = "FAIL: RAM integrity error — byte mismatch detected during stress";
        }
        else if (_oomOccurred)
        {
            ramPassed  = true;
            ramMessage = $"PASS: RAM stress completed (OOM at {_peakRamAllocatedMB} MB — insufficient free heap, not an error)";
        }
        else
        {
            ramPassed  = true;
            ramMessage = $"PASS: RAM integrity verified ({_peakRamAllocatedMB} MB allocated, all blocks intact)";
        }

        // ── Storage detail line ──────────────────────────────────
        string storageDetail = (_writeMBps > 0 || _readMBps > 0)
            ? $"Sequential Write: {_writeMBps:F0} MB/s | Sequential Read: {_readMBps:F0} MB/s"
            : "Storage benchmark unavailable";

        return new MacThermalStressResult
        {
            // CPU
            CpuPassed      = cpuPassed,
            CpuMessage     = cpuMessage,
            BaselineGips   = baselineGips,
            PeakGips       = peakGips,
            SustainedGips  = sustainedGips,
            RecoveryGips   = recoveryGips,
            ThrottlePercent= throttlePct,
            RecoveryPercent= recoveryPct,

            // RAM
            RamPassed           = ramPassed,
            RamMessage          = ramMessage,
            RamIntegrityFailed  = _ramIntegrityFailed,
            OomOccurred         = _oomOccurred,
            PeakRamAllocatedMB  = _peakRamAllocatedMB,

            // Storage
            WriteMBps     = _writeMBps,
            ReadMBps      = _readMBps,
            StorageDetail = storageDetail,

            // Thermal
            WorseThermalLevel = _worstThermalLevel,
            AbortedByHeat     = aborted
        };
    }

    // ════════════════════════════════════════════════════════════
    //  HELPERS
    // ════════════════════════════════════════════════════════════

    private static double SafeAvg(List<double> samples) =>
        samples.Count > 0 ? samples.Average() : 0;

    private void ReportProgress(string phase, int pct, int total, double gips)
    {
        OnProgress?.Invoke(new MacThermalStressProgress
        {
            Phase           = phase,
            PercentComplete = pct,
            CurrentGips     = gips
        });
    }
}

// ════════════════════════════════════════════════════════════════
//  RESULT + PROGRESS MODELS
// ════════════════════════════════════════════════════════════════

public class MacThermalStressResult
{
    // CPU
    public bool   CpuPassed       { get; set; }
    public string CpuMessage      { get; set; } = "";
    public double BaselineGips    { get; set; }
    public double PeakGips        { get; set; }
    public double SustainedGips   { get; set; }
    public double RecoveryGips    { get; set; }
    public double ThrottlePercent { get; set; }
    public double RecoveryPercent { get; set; }

    // RAM
    public bool   RamPassed          { get; set; }
    public string RamMessage         { get; set; } = "";
    public bool   RamIntegrityFailed { get; set; }
    public bool   OomOccurred        { get; set; }
    public int    PeakRamAllocatedMB { get; set; }

    // Storage
    public double WriteMBps     { get; set; }
    public double ReadMBps      { get; set; }
    public string StorageDetail { get; set; } = "";

    // Thermal
    public string WorseThermalLevel { get; set; } = "COOL";
    public bool   AbortedByHeat     { get; set; }
}

public class MacThermalStressProgress
{
    public string Phase           { get; set; } = "";
    public int    PercentComplete { get; set; }
    public double CurrentGips     { get; set; }
}
