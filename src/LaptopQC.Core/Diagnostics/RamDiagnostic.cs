#if WINDOWS
using LaptopQC.Core.Abstractions;
using LaptopQC.Hardware.Models;
using LaptopQC.Hardware.Providers;

namespace LaptopQC.Core.Diagnostics;

/// <summary>
/// Provides RAM detection and diagnostics
/// </summary>
public class RamDiagnostic : IRamDiagnostic
{
    private readonly IWmiProvider _wmi;

    public RamDiagnostic(IWmiProvider? wmiProvider = null)
    {
        _wmi = wmiProvider ?? new WmiProvider();
    }

    /// <summary>
    /// Gets detailed RAM information including all modules
    /// </summary>
    public RamInfo GetInfo()
    {
        var ramInfo = new RamInfo();
        var modules = new List<RamModule>();
        int slotIndex = 0;

        foreach (var obj in _wmi.Query("Win32_PhysicalMemory"))
        {
            var capacityBytes = _wmi.GetValue<long>(obj, "Capacity", 0);
            var capacityGB = capacityBytes / (1024L * 1024 * 1024);

            var module = new RamModule
            {
                Manufacturer = _wmi.GetValue<string>(obj, "Manufacturer", "Unknown") ?? "Unknown",
                PartNumber = _wmi.GetValue<string>(obj, "PartNumber", "") ?? "",
                SerialNumber = _wmi.GetValue<string>(obj, "SerialNumber", "") ?? "",
                CapacityGB = capacityGB,
                SpeedMHz = _wmi.GetValue<int>(obj, "Speed", 0),
                MemoryType = GetMemoryTypeString(_wmi.GetValue<int>(obj, "SMBIOSMemoryType", 0)),
                FormFactor = GetFormFactorString(_wmi.GetValue<int>(obj, "FormFactor", 0)),
                Slot = slotIndex++
            };

            modules.Add(module);
        }

        ramInfo.Modules = modules;
        ramInfo.UsedSlots = modules.Count;
        ramInfo.TotalCapacityGB = modules.Sum(m => m.CapacityGB);

        // Try to get total slot count from PhysicalMemoryArray
        foreach (var obj in _wmi.Query("Win32_PhysicalMemoryArray"))
        {
            ramInfo.TotalSlots = _wmi.GetValue<int>(obj, "MemoryDevices", modules.Count);
            break;
        }

        return ramInfo;
    }

    /// <summary>
    /// Validates RAM is functioning correctly
    /// </summary>
    public (bool IsHealthy, string Message) ValidateRam(RamInfo ramInfo)
    {
        if (ramInfo.TotalCapacityGB == 0)
            return (false, "No RAM detected");

        if (ramInfo.Modules.Count == 0)
            return (false, "No RAM modules found");

        if (ramInfo.TotalCapacityGB < 4)
            return (false, $"RAM capacity too low: {ramInfo.TotalCapacityGB}GB (minimum 4GB recommended)");

        return (true, $"RAM is functioning normally: {ramInfo.TotalCapacityGB}GB across {ramInfo.Modules.Count} module(s)");
    }

    private static string GetMemoryTypeString(int type)
    {
        return type switch
        {
            20 => "DDR",
            21 => "DDR2",
            24 => "DDR3",
            26 => "DDR4",
            34 => "DDR5",
            _ => $"Unknown ({type})"
        };
    }

    private static string GetFormFactorString(int formFactor)
    {
        return formFactor switch
        {
            8 => "DIMM",
            12 => "SODIMM",
            _ => $"Unknown ({formFactor})"
        };
    }
}
#endif
