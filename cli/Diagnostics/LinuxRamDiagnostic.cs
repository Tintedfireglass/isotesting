using System.Text.RegularExpressions;
using LaptopQC.Core.Abstractions;
using LaptopQC.Core.Diagnostics;
using LaptopQC.Hardware.Models;

namespace Pramaan.CLI.Diagnostics;

public class LinuxRamDiagnostic : IRamDiagnostic
{
    public RamInfo GetInfo()
    {
        var info = new RamInfo();

        try
        {
            // Total RAM from /proc/meminfo
            var meminfo = LinuxCommandRunner.ReadFile("/proc/meminfo");
            var totalMatch = Regex.Match(meminfo, @"^MemTotal:\s+(\d+)\s+kB", RegexOptions.Multiline);
            if (totalMatch.Success && long.TryParse(totalMatch.Groups[1].Value, out long kB))
            {
                long gb = (long)Math.Ceiling(kB / (1024.0 * 1024.0));
                long[] standardSizes = { 2, 4, 6, 8, 12, 16, 20, 24, 32, 48, 64, 96, 128, 256 };
                foreach (var size in standardSizes)
                {
                    // Allow up to 3GB of hardware reserved memory (iGPUs reserve a lot)
                    if (gb <= size && gb >= size - 3)
                    {
                        gb = size;
                        break;
                    }
                }
                info.TotalCapacityGB = gb;
            }

            // DIMM slots from dmidecode
            var dmi = LinuxCommandRunner.TryRun("dmidecode", "-t memory");
            if (!string.IsNullOrEmpty(dmi))
            {
                // Split into Memory Device blocks
                var blocks = Regex.Split(dmi, @"Memory Device\r?\n");
                int slot = 0;
                foreach (var block in blocks.Skip(1)) // Skip header
                {
                    var sizeStr = ParseField(block, "Size");
                    if (sizeStr == null || sizeStr.Equals("No Module Installed", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var module = new RamModule { Slot = slot };

                    // Parse size (e.g. "8192 MB" or "16 GB")
                    var sizeMatch = Regex.Match(sizeStr, @"(\d+)\s*(MB|GB)", RegexOptions.IgnoreCase);
                    if (sizeMatch.Success && long.TryParse(sizeMatch.Groups[1].Value, out long size))
                        module.CapacityGB = sizeMatch.Groups[2].Value.Equals("GB", StringComparison.OrdinalIgnoreCase) ? size : size / 1024;

                    // Memory type
                    module.MemoryType = ParseField(block, "Type") ?? "";
                    if (module.MemoryType.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                        module.MemoryType = "";

                    // Speed
                    var speedStr = ParseField(block, "Speed");
                    if (speedStr != null)
                    {
                        var speedMatch = Regex.Match(speedStr, @"(\d+)");
                        if (speedMatch.Success && int.TryParse(speedMatch.Groups[1].Value, out int spd))
                            module.SpeedMHz = spd;
                    }

                    module.Manufacturer = ParseField(block, "Manufacturer") ?? "";
                    module.FormFactor = ParseField(block, "Form Factor") ?? "";

                    info.Modules.Add(module);
                    slot++;
                }

                // If dmidecode gave modules, sum them up for exact physical capacity
                if (info.Modules.Count > 0)
                {
                    long trueCapacity = info.Modules.Sum(m => m.CapacityGB);
                    if (trueCapacity > 0)
                        info.TotalCapacityGB = trueCapacity;
                }
                else if (info.TotalCapacityGB > 0)
                {
                    // Synthesize one virtual module
                    info.Modules.Add(new RamModule
                    {
                        Slot = 0,
                        CapacityGB = info.TotalCapacityGB,
                        MemoryType = "DDR",
                        Manufacturer = "Unknown"
                    });
                }
            }
        }
        catch { /* Return what we have */ }

        return info;
    }

    public (bool IsHealthy, string Message) ValidateRam(RamInfo ramInfo)
    {
        if (ramInfo.TotalCapacityGB == 0) return (false, "No RAM detected");
        if (ramInfo.TotalCapacityGB < 2) return (false, $"Only {ramInfo.TotalCapacityGB}GB RAM — critically low");
        return (true, $"RAM: {ramInfo.TotalCapacityGB}GB Total");
    }

    private static string? ParseField(string block, string field)
    {
        var match = Regex.Match(block, $@"^\s*{Regex.Escape(field)}:\s*(.+)$", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
}
