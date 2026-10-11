using System.Text.RegularExpressions;
using LaptopQC.Core.Abstractions;
using LaptopQC.Hardware.Models;

namespace Pramaan.CLI.Diagnostics;

public class LinuxSystemDiagnostic : ISystemDiagnostic
{
    public SystemInfo GetInfo()
    {
        var info = new SystemInfo
        {
            ComputerName = Environment.MachineName
        };

        try
        {
            // OS Version from /etc/os-release
            var osRelease = LinuxCommandRunner.ReadFile("/etc/os-release");
            var prettyName = Regex.Match(osRelease, @"^PRETTY_NAME=""?(.+?)""?$", RegexOptions.Multiline);
            info.OsVersion = prettyName.Success ? prettyName.Groups[1].Value : $"Linux {Environment.OSVersion.Version}";

            // Manufacturer, Model, Serial from dmidecode
            var dmiSystem = LinuxCommandRunner.TryRun("dmidecode", "-t system");
            if (!string.IsNullOrEmpty(dmiSystem))
            {
                info.Manufacturer = ParseDmiField(dmiSystem, "Manufacturer") ?? "Unknown";
                info.Model = ParseDmiField(dmiSystem, "Product Name") ?? "Unknown";
                info.SerialNumber = ParseDmiField(dmiSystem, "Serial Number") ?? "";
            }

            // BIOS version
            var dmiBios = LinuxCommandRunner.TryRun("dmidecode", "-t bios");
            if (!string.IsNullOrEmpty(dmiBios))
                info.BiosVersion = ParseDmiField(dmiBios, "Version") ?? "";

            // MAC address — first non-loopback interface
            var ipLink = LinuxCommandRunner.TryRun("ip", "link");
            var macMatch = Regex.Matches(ipLink, @"link/ether\s+([0-9a-f:]{17})", RegexOptions.IgnoreCase);
            if (macMatch.Count > 0)
                info.MacAddress = macMatch[0].Groups[1].Value.ToUpperInvariant();
        }
        catch (Exception ex)
        {
            info.OsVersion = $"Linux (error: {ex.Message})";
        }

        return info;
    }

    private static string? ParseDmiField(string output, string field)
    {
        var match = Regex.Match(output, $@"^\s*{Regex.Escape(field)}:\s*(.+)$", RegexOptions.Multiline);
        if (!match.Success) return null;
        var value = match.Groups[1].Value.Trim();
        // Ignore placeholder values
        if (value.Equals("Not Specified", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase) ||
            value == "0" || string.IsNullOrWhiteSpace(value))
            return null;
        return value;
    }
}
