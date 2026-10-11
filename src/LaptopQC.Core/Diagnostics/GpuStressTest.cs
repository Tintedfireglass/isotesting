#if WINDOWS
using LaptopQC.Core.Abstractions;
using LaptopQC.Hardware.Providers;
using System.Diagnostics;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using Device = SharpDX.Direct3D11.Device;

namespace LaptopQC.Core.Diagnostics;

/// <summary>
/// GPU stress test with thermal monitoring for discrete GPUs (NVIDIA/AMD)
/// Uses DirectX 11 to stress the GPU with heavy rendering workload
/// </summary>
public class GpuStressTest : IGpuStressTest
{
    private readonly int _durationSeconds;
    private volatile bool _isRunning;
    private readonly object _dataLock = new();

    // Shared data
    private readonly List<double> _temps = new();
    private readonly List<double> _loads = new();
    private readonly List<double> _clocks = new();
    
    // GPU info
    private string? _gpuName;

    public event Action<GpuStressProgress>? OnProgress;

    private readonly ISensorProvider? _sensorProvider;

    public GpuStressTest(int durationSeconds = 30, ISensorProvider? sensorProvider = null)
    {
        _durationSeconds = durationSeconds;
        _sensorProvider = sensorProvider;
    }

    /// <summary>
    /// Runs the GPU stress test. Returns immediately with a skip result if no dGPU is detected.
    /// </summary>
    public async Task<GpuStressResult> RunAsync(CancellationToken cancellationToken = default)
    {
        // Hard timeout: GPU test must finish within duration + 30s grace period
        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(_durationSeconds + 30));
        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);
        var ct = combinedCts.Token;

        // Initialize sensors on background thread
        ISensorProvider? sensors = null;
        bool hasDiscreteGpu = false;

        await Task.Run(() =>
        {
            sensors = _sensorProvider ?? new SensorProvider();
            if (_sensorProvider == null) sensors.Initialize();
            hasDiscreteGpu = sensors.HasDiscreteGpu();
            _gpuName = sensors.GetDiscreteGpuName();
        }, ct);

        // Check for discrete GPU
        if (!hasDiscreteGpu || sensors == null)
        {
            return new GpuStressResult
            {
                Passed = true,
                Skipped = true,
                Message = "No discrete GPU detected (integrated GPU only)",
                GpuName = "None (Integrated Only)"
            };
        }

        var result = new GpuStressResult
        {
            GpuName = _gpuName ?? "Unknown GPU",
            DurationSeconds = _durationSeconds,
            StartTime = DateTime.Now
        };

        _isRunning = true;
        _temps.Clear();
        _loads.Clear();
        _clocks.Clear();

        var stopwatch = Stopwatch.StartNew();
        Device? device = null;
        Thread? stressThread = null;
        Thread? monitorThread = null;

        try
        {
            // Find and create D3D11 device on the discrete GPU
            device = CreateDeviceOnDiscreteGpu(out var adapterName);
            if (device == null)
            {
                return new GpuStressResult
                {
                    Passed = true,
                    Skipped = true,
                    Message = $"Could not create DirectX device on discrete GPU",
                    GpuName = _gpuName ?? "Unknown"
                };
            }

            result.GpuName = adapterName ?? _gpuName ?? "Unknown GPU";

            // 1. Start dedicated monitoring thread
            monitorThread = new Thread(() => MonitorLoop(stopwatch, ct, sensors))
            {
                Priority = ThreadPriority.AboveNormal,
                IsBackground = true,
                Name = "GpuMonitor"
            };
            monitorThread.Start();

            // Small delay to ensure monitor is running
            await Task.Delay(100, ct).ConfigureAwait(false);

            // 2. Start GPU stress thread
            var deviceCopy = device;
            stressThread = new Thread(() => DirectXStressLoop(deviceCopy, ct))
            {
                Priority = ThreadPriority.Normal,
                IsBackground = true,
                Name = "GpuStress"
            };
            stressThread.Start();

            // 3. Wait for test duration
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_durationSeconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }

            // 4. Signal threads to stop
            _isRunning = false;

            // Wait for threads to finish, then interrupt if stuck
            if (stressThread != null && !stressThread.Join(3000))
                stressThread.Interrupt();
            
            if (monitorThread != null && !monitorThread.Join(1500))
                monitorThread.Interrupt();
        }
        catch (Exception ex)
        {
            result.Passed = false;
            result.Message = $"GPU stress test error: {ex.Message}";
            _isRunning = false;
            return result;
        }
        finally
        {
            _isRunning = false;
            
            // Clean up DirectX resources in background so it doesn't block
            var devToDispose = device;
            if (devToDispose != null)
            {
                _ = Task.Run(() => 
                {
                    try { devToDispose.Dispose(); } catch { }
                });
            }
        }

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
            result.AvgLoad = _loads.Count > 0 ? _loads.Average() : 0;
            result.MaxLoad = _loads.Count > 0 ? _loads.Max() : 0;
        }

        // 6. Determine pass/fail
        var failures = new List<string>();

        if (result.MaxTemp > 95)
            failures.Add($"CRITICAL: GPU overheated ({result.MaxTemp:F1}°C > 95°C)");
        else if (result.MaxTemp > 90)
            failures.Add($"WARNING: High GPU temperature ({result.MaxTemp:F1}°C)");

        if (result.MaxClock > 0 && result.MaxTemp > 80)
        {
            double dropPercent = (1 - (result.MinClock / result.MaxClock)) * 100;
            if (dropPercent > 30)
                failures.Add($"THROTTLING: GPU speed dropped {dropPercent:F0}%");
        }

        // Check if test completed
        if (stopwatch.Elapsed.TotalSeconds < _durationSeconds * 0.9 && !cancellationToken.IsCancellationRequested)
            failures.Add("Test interrupted unexpectedly");

        result.Passed = failures.Count == 0;
        result.Message = result.Passed
            ? $"PASSED: {result.GpuName} | Max {result.MaxTemp:F1}°C, Avg Load {result.AvgLoad:F0}%, Speed ~{result.AvgClock:F0}MHz"
            : $"FAILED: {string.Join("; ", failures)}";

        if (_sensorProvider == null) sensors?.Dispose();
        return result;
    }

    /// <summary>
    /// Creates a DirectX 11 device specifically on the discrete GPU (NVIDIA/AMD)
    /// </summary>
    private Device? CreateDeviceOnDiscreteGpu(out string? adapterName)
    {
        adapterName = null;
        
        using var factory = new Factory1();
        
        // Enumerate adapters and find discrete GPU
        for (int i = 0; i < factory.GetAdapterCount1(); i++)
        {
            using var adapter = factory.GetAdapter1(i);
            var desc = adapter.Description1;
            var name = desc.Description;
            
            // Skip software adapters
            if ((desc.Flags & AdapterFlags.Software) != 0)
                continue;
            
            // Skip Intel (integrated)
            if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                continue;
            
            // Skip AMD iGPU patterns
            if (name.Contains("Radeon Graphics", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Radeon Vega", StringComparison.OrdinalIgnoreCase))
                continue;
            
            // Found a discrete GPU (NVIDIA or AMD dGPU)
            if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Radeon Pro", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("GeForce", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var device = new Device(
                        adapter,
                        DeviceCreationFlags.None,
                        FeatureLevel.Level_11_0,
                        FeatureLevel.Level_10_1,
                        FeatureLevel.Level_10_0);
                    
                    adapterName = name;
                    return device;
                }
                catch
                {
                    // Try next adapter
                    continue;
                }
            }
        }
        
        return null;
    }

    /// <summary>
    /// DirectX stress loop - creates and manipulates large GPU resources to stress the GPU
    /// </summary>
    private void DirectXStressLoop(Device device, CancellationToken ct)
    {
        try
        {
            var context = device.ImmediateContext;
            
            // Create multiple large textures to stress GPU memory and bandwidth
            const int textureSize = 2048;
            const int textureCount = 8;
            var textures = new List<Texture2D>();
            var rtvs = new List<RenderTargetView>();
            
            var textureDesc = new Texture2DDescription
            {
                Width = textureSize,
                Height = textureSize,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R32G32B32A32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None
            };
            
            for (int i = 0; i < textureCount; i++)
            {
                var texture = new Texture2D(device, textureDesc);
                textures.Add(texture);
                rtvs.Add(new RenderTargetView(device, texture));
            }
            
            // Create a render target
            var renderTargetDesc = new Texture2DDescription
            {
                Width = textureSize,
                Height = textureSize,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
                CpuAccessFlags = CpuAccessFlags.None
            };
            
            using var renderTarget = new Texture2D(device, renderTargetDesc);
            using var rtv = new RenderTargetView(device, renderTarget);
            
            // Stress loop - continuously clear and copy textures
            int iteration = 0;
            while (_isRunning && !ct.IsCancellationRequested)
            {
                // Clear all render target views with varying colors
                for (int i = 0; i < textureCount; i++)
                {
                    float r = (float)Math.Sin((iteration + i) * 0.1) * 0.5f + 0.5f;
                    float g = (float)Math.Cos((iteration + i) * 0.1) * 0.5f + 0.5f;
                    float b = (float)Math.Sin((iteration + i) * 0.2) * 0.5f + 0.5f;
                    
                    context.ClearRenderTargetView(rtvs[i], new RawColor4(r, g, b, 1.0f));
                }
                
                // Copy between textures to stress memory bandwidth
                for (int i = 0; i < textureCount - 1; i++)
                {
                    context.CopyResource(textures[i], textures[(i + 1) % textureCount]);
                }
                
                // Do rapid clears to generate more GPU work
                for (int i = 0; i < 100; i++)
                {
                    context.ClearRenderTargetView(rtv, new RawColor4(
                        (float)(iteration + i) / 1000.0f % 1.0f,
                        (float)(iteration + i * 2) / 1000.0f % 1.0f,
                        (float)(iteration + i * 3) / 1000.0f % 1.0f,
                        1.0f));
                }
                
                context.Flush();
                iteration++;
            }
            
            // Cleanup
            foreach (var rtvItem in rtvs) rtvItem.Dispose();
            foreach (var tex in textures) tex.Dispose();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(LaptopQC.Core.Models.AppPaths.AppDataDir, "gpu_stress_error.log"), ex.ToString());
        }
    }

    private void MonitorLoop(Stopwatch stopwatch, CancellationToken ct, ISensorProvider sensors)
    {
        var logPath = Path.Combine(LaptopQC.Core.Models.AppPaths.AppDataDir, "gpu_stress_debug.log");

        try
        {
            using var log = new StreamWriter(logPath, append: false);
            log.WriteLine("Time,Temp(C),Load(%),Clock(MHz)");
            log.WriteLine($"DEBUG: GPU MonitorLoop started for {_gpuName}");
            log.Flush();

            while (_isRunning && !ct.IsCancellationRequested)
            {
                try
                {
                    sensors.Update();
                    var temp = sensors.GetGpuTemperature();
                    var load = sensors.GetGpuLoad();
                    var clock = sensors.GetGpuClockSpeed();

                    lock (_dataLock)
                    {
                        if (temp.HasValue) _temps.Add(temp.Value);
                        if (load.HasValue) _loads.Add(load.Value);
                        if (clock.HasValue) _clocks.Add(clock.Value);

                        log.WriteLine($"{stopwatch.Elapsed.TotalSeconds:F1},{temp:F1},{load:F1},{clock:F0}");
                        log.Flush();

                        OnProgress?.Invoke(new GpuStressProgress
                        {
                            ElapsedSeconds = (int)stopwatch.Elapsed.TotalSeconds,
                            TotalSeconds = _durationSeconds,
                            PercentComplete = Math.Min(100, (int)(stopwatch.Elapsed.TotalSeconds / _durationSeconds * 100)),
                            CurrentTemp = temp ?? 0,
                            CurrentLoad = load ?? 0,
                            CurrentClock = clock ?? 0,
                            GpuName = _gpuName ?? "Unknown"
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

            log.WriteLine("DEBUG: GPU MonitorLoop exiting");
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(LaptopQC.Core.Models.AppPaths.AppDataDir, "gpu_monitor_crash.log"), ex.ToString());
        }
    }
}

/// <summary>
/// Result of GPU stress test
/// </summary>
public class GpuStressResult
{
    public bool Passed { get; set; }
    public bool Skipped { get; set; }
    public string Message { get; set; } = "";
    public string GpuName { get; set; } = "";
    public int DurationSeconds { get; set; }
    public double ActualDurationSeconds { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public double MaxTemp { get; set; }
    public double AvgTemp { get; set; }
    public double MaxLoad { get; set; }
    public double AvgLoad { get; set; }
    public double MinClock { get; set; }
    public double MaxClock { get; set; }
    public double AvgClock { get; set; }

    /// <summary>
    /// Get a detailed summary of the GPU stress test results
    /// </summary>
    public string GetDetailedSummary()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== GPU Stress Test Results ===");
        sb.AppendLine($"GPU: {GpuName}");
        
        if (Skipped)
        {
            sb.AppendLine($"Status: SKIPPED - {Message}");
            return sb.ToString();
        }
        
        sb.AppendLine($"Verdict: {(Passed ? "PASS" : "FAIL")} - {Message}");
        sb.AppendLine($"Duration: {ActualDurationSeconds:F1}s");
        sb.AppendLine($"Temperature: {AvgTemp:F0}°C avg, {MaxTemp:F0}°C max");
        sb.AppendLine($"Load: {AvgLoad:F0}% avg, {MaxLoad:F0}% max");
        sb.AppendLine($"Clock Speed: {AvgClock:F0} MHz avg ({MinClock:F0} - {MaxClock:F0} MHz)");
        
        return sb.ToString();
    }
}

/// <summary>
/// Progress update during GPU stress test
/// </summary>
public class GpuStressProgress
{
    public int ElapsedSeconds { get; set; }
    public int TotalSeconds { get; set; }
    public int PercentComplete { get; set; }
    public double CurrentTemp { get; set; }
    public double CurrentLoad { get; set; }
    public double CurrentClock { get; set; }
    public string GpuName { get; set; } = "";
}
#endif
