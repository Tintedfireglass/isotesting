#if WINDOWS
using LaptopQC.Core.Abstractions;
using LaptopQC.Hardware.Models;
using LaptopQC.Hardware.Providers;

namespace LaptopQC.Core.Diagnostics;

/// <summary>
/// Provides system information detection
/// </summary>
public class SystemDiagnostic : ISystemDiagnostic
{
    private readonly IWmiProvider _wmi;

    public SystemDiagnostic(IWmiProvider? wmiProvider = null)
    {
        _wmi = wmiProvider ?? new WmiProvider();
    }

    /// <summary>
    /// Gets basic system information
    /// </summary>
    public SystemInfo GetInfo()
    {
        var systemInfo = new SystemInfo
        {
            ComputerName = Environment.MachineName,
            OsVersion = Environment.OSVersion.ToString(),
            ScanTimestamp = DateTime.UtcNow
        };

        // Get computer system info
        foreach (var obj in _wmi.Query("Win32_ComputerSystem"))
        {
            systemInfo.Manufacturer = _wmi.GetValue<string>(obj, "Manufacturer", "Unknown") ?? "Unknown";
            systemInfo.Model = _wmi.GetValue<string>(obj, "Model", "Unknown") ?? "Unknown";
            break;
        }

        systemInfo.ProductVersion = GetOptionalModelName("Win32_ComputerSystemProduct", "Version");
        systemInfo.SystemFamily = GetOptionalModelName("Win32_ComputerSystem", "SystemFamily");

        // Get BIOS info
        foreach (var obj in _wmi.Query("Win32_BIOS"))
        {
            systemInfo.BiosVersion = _wmi.GetValue<string>(obj, "SMBIOSBIOSVersion", "") ?? "";
            systemInfo.SerialNumber = _wmi.GetValue<string>(obj, "SerialNumber", "") ?? "";
            systemInfo.BiosManufacturer = _wmi.GetValue<string>(obj, "Manufacturer", "") ?? "";
            // ReleaseDate is a WMI DMTF datetime string — convert to DateTime if present
            var releaseDateStr = _wmi.GetValue<string>(obj, "ReleaseDate", "");
            if (!string.IsNullOrWhiteSpace(releaseDateStr))
            {
                try
                {
                    systemInfo.BiosReleaseDate = System.Management.ManagementDateTimeConverter.ToDateTime(releaseDateStr);
                }
                catch
                {
                    systemInfo.BiosReleaseDate = null;
                }
            }
            break;
        }

        // Get MAC address from primary network adapter
        foreach (var obj in _wmi.Query("Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE"))
        {
            var mac = _wmi.GetValue<string>(obj, "MACAddress", "");
            if (!string.IsNullOrEmpty(mac))
            {
                systemInfo.MacAddress = mac;
                break; // Get first active adapter's MAC
            }
        }

        return systemInfo;
    }

    private string? GetOptionalModelName(string wmiClass, string property)
    {
        try
        {
            foreach (var obj in _wmi.Query(wmiClass, new[] { property }))
            {
                using (obj)
                {
                    var value = _wmi.GetValue<string>(obj, property)?.Trim();
                    if (string.IsNullOrWhiteSpace(value)) return null;

                    return value.ToUpperInvariant() switch
                    {
                        "NONE" or "TO BE FILLED BY O.E.M." or "SYSTEM VERSION"
                            or "DEFAULT STRING" or "NOT APPLICABLE" => null,
                        _ => value
                    };
                }
            }
        }
        catch
        {
            // Optional model names must never prevent report collection.
        }

        return null;
    }
}
#endif
