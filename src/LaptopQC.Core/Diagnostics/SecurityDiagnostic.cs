#if WINDOWS
using Microsoft.Win32;
using System.Management;
using System.Globalization;
using System.Runtime.InteropServices;

namespace LaptopQC.Core.Diagnostics;

public class SecurityDiagnostic
{
    private const string WindowsAppId = "55c92734-d682-4d71-983e-d6ec3f16059f";

    public WindowsActivationStatus GetWindowsActivationStatus()
    {
        try
        {
            var statuses = new List<WindowsActivationStatus>();
            var query =
                "SELECT LicenseStatus, Name, Description, PartialProductKey " +
                "FROM SoftwareLicensingProduct " +
                $"WHERE ApplicationID='{WindowsAppId}' AND PartialProductKey IS NOT NULL";

            using var searcher = new ManagementObjectSearcher(query);
            foreach (ManagementObject obj in searcher.Get())
            {
                var status = new WindowsActivationStatus
                {
                    LicenseStatus = ToInt(obj["LicenseStatus"]),
                    ProductName = obj["Name"]?.ToString(),
                    Description = obj["Description"]?.ToString(),
                    PartialProductKey = obj["PartialProductKey"]?.ToString()
                };

                status.StatusLabel = MapLicenseStatus(status.LicenseStatus);
                status.IsActivated = status.LicenseStatus == 1;
                statuses.Add(status);
            }

            if (statuses.Count == 0)
                return new WindowsActivationStatus { StatusLabel = "Unknown", IsActivated = false };

            return statuses.FirstOrDefault(s => s.IsActivated) ?? statuses.First();
        }
        catch (Exception ex)
        {
            return new WindowsActivationStatus
            {
                StatusLabel = "Error",
                IsActivated = false,
                Error = ex.Message
            };
        }
    }

    public AntivirusStatus GetAntivirusStatus()
    {
        var status = new AntivirusStatus();

        try
        {
            status.OverallHealth = GetWscHealth(out var health);
            status.IsHealthy = health == WscSecurityProviderHealth.Good;
        }
        catch (Exception ex)
        {
            status.Error = ex.Message;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\SecurityCenter2",
                "SELECT displayName, productState FROM AntiVirusProduct");

            foreach (ManagementObject obj in searcher.Get())
            {
                var info = new AntivirusProductInfo
                {
                    Name = obj["displayName"]?.ToString() ?? "Unknown"
                };

                if (obj["productState"] != null)
                {
                    var state = ToInt(obj["productState"]);
                    info.ProductStateHex = state.HasValue ? $"0x{state.Value:X6}" : null;
                }

                status.Products.Add(info);
            }

            status.HasAnyProduct = status.Products.Count > 0;
            if (!status.HasAnyProduct && string.IsNullOrWhiteSpace(status.OverallHealth))
            {
                status.OverallHealth = "Not detected";
            }
        }
        catch (Exception ex)
        {
            status.Error = ex.Message;
        }

        if (string.IsNullOrWhiteSpace(status.OverallHealth))
            status.OverallHealth = "Unknown";

        return status;
    }

    public DateTime? GetWindowsLastUpdatedAt()
    {
        try
        {
            // Best-effort. Some images do not have this value set.
            const string keyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\Results\Install";
            var raw = Registry.GetValue(keyPath, "LastSuccessTime", null) as string;
            if (!string.IsNullOrWhiteSpace(raw) && DateTime.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsed))
            {
                return parsed;
            }
        }
        catch { /* Best-effort only */ }

        try
        {
            DateTime? latest = null;

            using var searcher = new ManagementObjectSearcher("SELECT InstalledOn FROM Win32_QuickFixEngineering");
            foreach (ManagementObject obj in searcher.Get())
            {
                var installedOn = obj["InstalledOn"]?.ToString();
                if (string.IsNullOrWhiteSpace(installedOn)) continue;

                if (!TryParseInstalledOn(installedOn, out var dt)) continue;

                if (latest == null || dt > latest.Value)
                    latest = dt;
            }

            return latest;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseInstalledOn(string installedOn, out DateTime parsed)
    {
        // Common format: "M/d/yyyy" or "MM/dd/yyyy"
        if (DateTime.TryParse(installedOn, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out parsed))
            return true;

        if (DateTime.TryParse(installedOn, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed))
            return true;

        if (DateTime.TryParse(installedOn, new CultureInfo("en-US"), DateTimeStyles.AssumeLocal, out parsed))
            return true;

        return false;
    }

    private static int? ToInt(object? value)
    {
        if (value == null) return null;
        try
        {
            return Convert.ToInt32(value);
        }
        catch
        {
            return null;
        }
    }

    private static string MapLicenseStatus(int? status)
    {
        return status switch
        {
            0 => "Unlicensed",
            1 => "Licensed",
            2 => "OOB Grace",
            3 => "OOT Grace",
            4 => "Non-genuine Grace",
            5 => "Notification",
            6 => "Extended Grace",
            _ => "Unknown"
        };
    }

    private static string GetWscHealth(out WscSecurityProviderHealth health)
    {
        var hr = WscGetSecurityProviderHealth((int)WscSecurityProvider.Antivirus, out var result);
        if (hr != 0)
        {
            health = WscSecurityProviderHealth.NotMonitored;
            return "Unknown";
        }

        health = (WscSecurityProviderHealth)result;
        return health switch
        {
            WscSecurityProviderHealth.Good => "Good",
            WscSecurityProviderHealth.NotMonitored => "Not monitored",
            WscSecurityProviderHealth.Poor => "Poor",
            WscSecurityProviderHealth.Snooze => "Snoozed",
            _ => "Unknown"
        };
    }

    [DllImport("wscapi.dll")]
    private static extern int WscGetSecurityProviderHealth(int providers, out int health);

    private enum WscSecurityProvider
    {
        Antivirus = 0x4
    }

    private enum WscSecurityProviderHealth
    {
        Good = 0,
        NotMonitored = 1,
        Poor = 2,
        Snooze = 3
    }
}
#endif
