using System.Text.RegularExpressions;
using LaptopQC.Core.Abstractions;
using LaptopQC.Core.Diagnostics;
using LaptopQC.Hardware.Models;

namespace Pramaan.CLI.Diagnostics;

public class LinuxCpuDiagnostic : ICpuDiagnostic
{
    public CpuInfo GetInfo()
    {
        var info = new CpuInfo();

        try
        {
            var lscpu = LinuxCommandRunner.TryRun("lscpu", "");

            info.Name = ParseField(lscpu, "Model name") ?? "Unknown CPU";
            info.Manufacturer = info.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel"
                              : info.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? "AMD"
                              : "Unknown";

            if (int.TryParse(ParseField(lscpu, "Core(s) per socket"), out int cores) &&
                int.TryParse(ParseField(lscpu, "Socket(s)"), out int sockets))
                info.Cores = cores * sockets;

            if (int.TryParse(ParseField(lscpu, "CPU(s)"), out int threads))
                info.Threads = threads;

            // Max MHz
            var maxMhzStr = ParseField(lscpu, "CPU max MHz") ?? ParseField(lscpu, "CPU MHz");
            if (maxMhzStr != null && double.TryParse(maxMhzStr, out double mhz))
                info.MaxClockSpeedMHz = (int)mhz;

            // Architecture
            var arch = ParseField(lscpu, "Architecture");
            if (arch != null) info.Architecture = arch;
        }
        catch { /* Return whatever we got */ }

        return info;
    }

    public (bool IsHealthy, string Message) ValidateCpu(CpuInfo cpuInfo)
    {
        if (string.IsNullOrWhiteSpace(cpuInfo.Name) || cpuInfo.Name == "Unknown CPU")
            return (false, "CPU could not be detected");
        if (cpuInfo.Cores == 0)
            return (false, "CPU core count could not be determined");
        return (true, $"CPU OK: {cpuInfo.Name}");
    }

    private static string? ParseField(string lscpu, string field)
    {
        var match = Regex.Match(lscpu, $@"^{Regex.Escape(field)}:\s*(.+)$", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
}
