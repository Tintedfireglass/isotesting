using LaptopQC.Hardware.Models;

namespace LaptopQC.Core.Abstractions;

// ──────────────────────────────────────────────────────────────
// Diagnostic Interfaces — platform-neutral contracts for all 
// hardware detection and validation services.
//
// Windows: implemented by existing classes in LaptopQC.Core.Diagnostics
// macOS:   future implementations in Diagnostics/macOS/
// ──────────────────────────────────────────────────────────────

/// <summary>
/// System information (hostname, model, serial, MAC address)
/// </summary>
public interface ISystemDiagnostic
{
    SystemInfo GetInfo();
}

/// <summary>
/// CPU detection and validation
/// </summary>
public interface ICpuDiagnostic
{
    CpuInfo GetInfo();
    (bool IsHealthy, string Message) ValidateCpu(CpuInfo cpuInfo);
}

/// <summary>
/// RAM detection and validation
/// </summary>
public interface IRamDiagnostic
{
    RamInfo GetInfo();
    (bool IsHealthy, string Message) ValidateRam(RamInfo ramInfo);
}

/// <summary>
/// Storage device detection and SMART health
/// </summary>
public interface IStorageDiagnostic
{
    Diagnostics.StorageInfo GetInfo();
    (bool IsHealthy, string Message) ValidateStorage(Diagnostics.StorageInfo info);
}

/// <summary>
/// Battery detection and health diagnostics
/// </summary>
public interface IBatteryDiagnostic
{
    Diagnostics.BatteryInfo GetInfo();
    (bool IsHealthy, string Message) ValidateBattery(Diagnostics.BatteryInfo info);
}

/// <summary>
/// Peripheral and port device detection (USB, displays, audio, network, camera, input)
/// </summary>
public interface IDeviceDiagnostic
{
    DevicesInfo GetInfo();
    (bool IsHealthy, string Message) ValidateDevices(DevicesInfo info);
}

/// <summary>
/// SMART disk testing service
/// </summary>
public interface ISmartTestService
{
    bool IsAvailable { get; }
    List<Diagnostics.SmartDriveInfo> GetTestableDevices();
    Diagnostics.SmartDriveInfo? GetDeviceInfo(string devicePath);
    Task<Diagnostics.SmartTestResultInfo> RunShortTestAsync(string devicePath, IProgress<Diagnostics.SmartTestProgress>? progress = null, string? deviceType = null);
    Diagnostics.SmartHealthCheckResult QuickHealthCheck();
}

/// <summary>
/// Audio/Video testing (speakers, microphone, camera, 3.5mm jack)
/// </summary>
public interface IAudioVideoTestService : IDisposable
{
    void TestSpeaker(bool isLeft);
    void StartOneShotMicTest();
    void StopMicTest();
    void PlaybackMicRecording();
    (bool IsConnected, string DeviceName) GetHeadphoneStatus();
    bool PlayTestSoundToHeadphones();
    void StopJackPlayback();
    void LaunchCameraApp();
}

/// <summary>
/// CPU stress test with thermal monitoring
/// </summary>
public interface ICpuStressTest
{
    event Action<Diagnostics.StressTestProgress>? OnProgress;
    Task<Diagnostics.CpuStressResult> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// GPU stress test with DirectX/Metal workload
/// </summary>
public interface IGpuStressTest
{
    event Action<Diagnostics.GpuStressProgress>? OnProgress;
    Task<Diagnostics.GpuStressResult> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// RAM stress test for memory stability
/// </summary>
public interface IRamStressTest
{
    Action<Diagnostics.RamStressTest.ProgressInfo>? OnProgress { get; set; }
    Task<Diagnostics.RamStressTest.RamStressResult> RunAsync();
}
