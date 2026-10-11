#if WINDOWS
using LaptopQC.Core.Abstractions;
using LaptopQC.Hardware.Models;
using LaptopQC.Hardware.Providers;

namespace LaptopQC.Core.Diagnostics;

/// <summary>
/// Provides CPU detection and diagnostics
/// </summary>
public class CpuDiagnostic : ICpuDiagnostic
{
    private readonly IWmiProvider _wmi;

    public CpuDiagnostic(IWmiProvider? wmiProvider = null)
    {
        _wmi = wmiProvider ?? new WmiProvider();
    }

    /// <summary>
    /// Gets detailed CPU information
    /// </summary>
    public CpuInfo GetInfo()
    {
        var cpuInfo = new CpuInfo();

        foreach (var obj in _wmi.Query("Win32_Processor"))
        {
            cpuInfo.Name = _wmi.GetValue<string>(obj, "Name", "Unknown") ?? "Unknown";
            cpuInfo.Manufacturer = _wmi.GetValue<string>(obj, "Manufacturer", "Unknown") ?? "Unknown";
            cpuInfo.Cores = _wmi.GetValue<int>(obj, "NumberOfCores", 0);
            cpuInfo.Threads = _wmi.GetValue<int>(obj, "NumberOfLogicalProcessors", 0);
            cpuInfo.MaxClockSpeedMHz = _wmi.GetValue<int>(obj, "MaxClockSpeed", 0);
            cpuInfo.CurrentClockSpeedMHz = _wmi.GetValue<int>(obj, "CurrentClockSpeed", 0);
            cpuInfo.ProcessorId = _wmi.GetValue<string>(obj, "ProcessorId", "") ?? "";
            cpuInfo.L2CacheSizeKB = _wmi.GetValue<int>(obj, "L2CacheSize", 0);
            cpuInfo.L3CacheSizeKB = _wmi.GetValue<int>(obj, "L3CacheSize", 0);

            // Map architecture number to string
            var archNumber = _wmi.GetValue<int>(obj, "Architecture", 0);
            cpuInfo.Architecture = archNumber switch
            {
                0 => "x86",
                5 => "ARM",
                9 => "x64",
                12 => "ARM64",
                _ => "Unknown"
            };

            break; // Only first processor
        }

        return cpuInfo;
    }

    /// <summary>
    /// Validates CPU is functioning correctly
    /// </summary>
    public (bool IsHealthy, string Message) ValidateCpu(CpuInfo cpuInfo)
    {
        if (cpuInfo.Cores == 0)
            return (false, "CPU core count could not be determined");

        if (cpuInfo.MaxClockSpeedMHz == 0)
            return (false, "CPU clock speed could not be determined");

        if (cpuInfo.TemperatureCelsius.HasValue && cpuInfo.TemperatureCelsius > 90)
            return (false, $"CPU temperature is too high: {cpuInfo.TemperatureCelsius}°C");

        return (true, "CPU is functioning normally");
    }
}
#endif
