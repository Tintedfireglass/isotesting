using System.Text.RegularExpressions;
using LaptopQC.Core.Abstractions;
using LaptopQC.Core.Diagnostics;

namespace Pramaan.CLI.Diagnostics;

/// <summary>
/// Linux implementation of ISmartTestService using the smartctl CLI tool.
/// Mirrors SmartTestService.cs (Windows) in behaviour:
///   - QuickHealthCheck(): scans all drives, reads SMART health + temperature
///   - RunShortTestAsync(): starts a short self-test and polls for completion
/// </summary>
public class LinuxSmartTestService : ISmartTestService
{
    private readonly string _smartctlPath;

    public LinuxSmartTestService()
    {
        // 1. Extract embedded smartctl if available
        string embeddedPath = Path.Combine(Path.GetTempPath(), "pramaan_smartctl");
        try
        {
            using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("smartctl-linux-x64");
            if (stream != null)
            {
                using var fs = new FileStream(embeddedPath, FileMode.Create, FileAccess.Write);
                stream.CopyTo(fs);
                fs.Close();
                LinuxCommandRunner.TryRun("chmod", $"+x {embeddedPath}");
            }
        }
        catch { }

        // 2. Check for bundled smartctl first, then fallback to system paths
        string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "smartctl-linux-x64");
        string localPathGeneric = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "smartctl");

        _smartctlPath = File.Exists(embeddedPath) ? embeddedPath
                      : File.Exists(localPath) ? localPath
                      : File.Exists(localPathGeneric) ? localPathGeneric
                      : File.Exists("/usr/bin/smartctl") ? "/usr/bin/smartctl"
                      : File.Exists("/usr/local/bin/smartctl") ? "/usr/local/bin/smartctl"
                      : "smartctl";
    }

    // ── ISmartTestService ────────────────────────────────────────

    public bool IsAvailable
    {
        get
        {
            try
            {
                var out_ = LinuxCommandRunner.TryRun(_smartctlPath, "--version", 5000);
                return out_.Contains("smartctl");
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Returns all drives that smartctl can see (excludes loop/ram devices).
    /// </summary>
    public List<SmartDriveInfo> GetTestableDevices()
    {
        var devices = new List<SmartDriveInfo>();

        try
        {
            var scanOut = LinuxCommandRunner.TryRun(_smartctlPath, "--scan");
            foreach (var line in scanOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // Format:  /dev/sda -d scsi # /dev/sda, SCSI device
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;

                var devPath = parts[0].Trim();
                if (string.IsNullOrWhiteSpace(devPath)) continue;
                // Skip loop and ram devices
                if (devPath.Contains("/loop") || devPath.Contains("/ram")) continue;

                // Check if smartctl provided a device type argument (e.g. -d megaraid,0)
                string devArgs = "";
                int typeIdx = line.IndexOf(" -d ");
                if (typeIdx != -1)
                {
                    int commentIdx = line.IndexOf(" # ");
                    if (commentIdx > typeIdx)
                        devArgs = line.Substring(typeIdx, commentIdx - typeIdx).Trim();
                    else
                        devArgs = line.Substring(typeIdx).Trim();
                }

                var info = BuildDriveInfo(devPath, devArgs);
                if (info != null)
                {
                    if (info.Model?.Contains("usb", StringComparison.OrdinalIgnoreCase) == true)
                        continue;
                    devices.Add(info);
                }
            }

            // Fallback for Hardware RAID controllers that are missed by --scan
            if (devices.Count == 0 || IsHardwareRaidPresent())
            {
                devices.AddRange(ProbeHardwareRaidDevices());
            }
        }
        catch { /* Return whatever we got */ }

        return devices;
    }

    public SmartDriveInfo? GetDeviceInfo(string devicePath)
        => BuildDriveInfo(devicePath);

    /// <summary>
    /// Performs a quick SMART health check — no self-test, just attribute read.
    /// </summary>
    public SmartHealthCheckResult QuickHealthCheck()
    {
        var result = new SmartHealthCheckResult
        {
            CheckTime = DateTime.Now,
            SmartctlAvailable = IsAvailable
        };

        if (!result.SmartctlAvailable)
        {
            result.OverallHealthy = false;
            result.Message = "smartctl not found. Install smartmontools (sudo apt install smartmontools).";
            return result;
        }

        var devices = GetTestableDevices();
        result.Devices = devices;

        if (devices.Count == 0)
        {
            result.OverallHealthy = true;
            result.Message = "No SMART-capable drives found";
            return result;
        }

        bool allHealthy = devices.All(d => d.HealthPassed);
        bool anyFailing = devices.Any(d => !d.HealthPassed || d.HealthScore < 50);

        result.OverallHealthy = allHealthy;
        if (anyFailing)
            result.Message = "One or more drives have critical SMART issues!";
        else if (!allHealthy)
            result.Message = "Some drives have SMART warnings — review recommended";
        else
            result.Message = $"All {devices.Count} drive(s) healthy";

        return result;
    }

    /// <summary>
    /// Starts a SMART short self-test and polls until complete or timeout.
    /// Short test typically completes in 1–2 minutes.
    /// </summary>
    public async Task<SmartTestResultInfo> RunShortTestAsync(
        string devicePath,
        IProgress<SmartTestProgress>? progress = null,
        string? deviceType = null)
    {
        var result = new SmartTestResultInfo
        {
            DevicePath = devicePath,
            TestType   = "Short",
            StartTime  = DateTime.Now
        };

        var args = string.IsNullOrWhiteSpace(deviceType) ? "" : $"-d {deviceType} ";
        var startOut = LinuxCommandRunner.TryRun(_smartctlPath, $"-t short {args}{devicePath}");
        if (startOut.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            startOut.Contains("Unable to", StringComparison.OrdinalIgnoreCase) ||
            startOut.Contains("failed to", StringComparison.OrdinalIgnoreCase))
        {
            // Distinguish inconclusive vs hard failure
            result.Success = false;
            result.Message = ExtractSmartError(startOut);
            result.EndTime = DateTime.Now;
            return result;
        }

        if (string.IsNullOrWhiteSpace(startOut))
        {
            result.Success = false;
            result.Message = "Failed to start self-test (no output — run with sudo?)";
            result.EndTime = DateTime.Now;
            return result;
        }

        progress?.Report(new SmartTestProgress
        {
            DevicePath      = devicePath,
            Status          = "Self-test started",
            PercentComplete = 0,
            IsRunning       = true
        });

        // Poll every 5 seconds for up to 3 minutes (NVMe short tests finish in <2 min)
        const int maxPolls = 36;
        for (int i = 0; i < maxPolls; i++)
        {
            await Task.Delay(5000);

            var logOut = LinuxCommandRunner.TryRun(_smartctlPath, $"-l selftest {args}{devicePath}");

            int pct = 0;
            string status = "Testing...";
            bool matchFound = false;

            // ── ATA format: "# 1  Short offline  Completed without error  00%  12345  -"
            // ATA entries use "# N" prefix and have a "XX%" remaining column.
            var ataMatch = Regex.Match(logOut,
                @"#\s*1\s+\S+\s+\S+\s+(\S.*?)\s{2,}(\d+)%",
                RegexOptions.Multiline);

            if (ataMatch.Success)
            {
                status = ataMatch.Groups[1].Value.Trim();
                int.TryParse(ataMatch.Groups[2].Value, out pct);
                matchFound = true;
            }
            else
            {
                // ── NVMe format (entries are 0-indexed, no "#" prefix):
                //   Self-test status: Short self-test in progress (14% completed)   ← real-time
                //    0   Short             Completed without error   8043   -   -   -   -    -  ← log entry
                //
                // IMPORTANT: while a new test is running the OLD completed entry (index 0)
                // is still in the log. Check the status line FIRST so we don't falsely
                // declare completion from the previous test's entry.

                var nvmeInProgress = Regex.Match(logOut,
                    @"Self-test status:.*in progress.*\((\d+)%\s+completed\)",
                    RegexOptions.IgnoreCase);

                if (nvmeInProgress.Success)
                {
                    // Test is still running — report real percentage and keep polling
                    int.TryParse(nvmeInProgress.Groups[1].Value, out int nvmePct);
                    progress?.Report(new SmartTestProgress
                    {
                        DevicePath      = devicePath,
                        Status          = "Self-test in progress...",
                        PercentComplete = nvmePct,
                        IsRunning       = true
                    });
                    continue;
                }

                // No in-progress line → check entry 0 for the completed result
                var nvmeMatch = Regex.Match(logOut,
                    @"^\s*0\s+Short\s+(.+?)\s{2,}",
                    RegexOptions.Multiline | RegexOptions.IgnoreCase);

                if (nvmeMatch.Success)
                {
                    status = nvmeMatch.Groups[1].Value.Trim();
                    pct = 0; // NVMe log entries don't have a % column
                    matchFound = true;
                }
            }

            // Nothing matched yet — test may not have started logging
            if (!matchFound)
            {
                if (i < 4)
                {
                    progress?.Report(new SmartTestProgress
                    {
                        DevicePath      = devicePath,
                        Status          = "Waiting for test to begin...",
                        PercentComplete = 0,
                        IsRunning       = true
                    });
                    continue;
                }

                // Check if the log says nothing has ever run
                bool noEntries = logOut.Contains("No self-tests have been logged",
                    StringComparison.OrdinalIgnoreCase);
                if (noEntries)
                {
                    result.Success = true;
                    result.Passed  = false;
                    result.Message = "Self-test could not be verified (no log entry)";
                    result.EndTime = DateTime.Now;
                    return result;
                }
                continue;
            }

            bool completed = status.Contains("Completed", StringComparison.OrdinalIgnoreCase) ||
                             status.Contains("without error", StringComparison.OrdinalIgnoreCase);
            bool failed    = status.Contains("Failed", StringComparison.OrdinalIgnoreCase) ||
                             (status.Contains("error", StringComparison.OrdinalIgnoreCase) &&
                              !status.Contains("without error", StringComparison.OrdinalIgnoreCase));
            bool inProgress = pct > 0 || status.Contains("progress", StringComparison.OrdinalIgnoreCase);

            progress?.Report(new SmartTestProgress
            {
                DevicePath      = devicePath,
                Status          = status,
                PercentComplete = pct > 0 ? 100 - pct : (completed ? 100 : 50),
                IsRunning       = inProgress
            });

            if (completed || failed || (!inProgress && matchFound))
            {
                bool passed = status.Contains("without error", StringComparison.OrdinalIgnoreCase);
                result.Success  = true;
                result.Passed   = passed;
                result.Message  = passed ? "Self-Test Passed" : $"Self-Test Failed: {status}";
                result.EndTime  = DateTime.Now;
                return result;
            }
        }

        result.Success = false;
        result.Message = "Self-test timed out";
        result.EndTime = DateTime.Now;
        return result;
    }

    // ── Helpers ─────────────────────────────────────────────────

    private SmartDriveInfo? BuildDriveInfo(string devicePath, string? deviceArgs = null)
    {
        try
        {
            var args = string.IsNullOrWhiteSpace(deviceArgs) ? "" : $" {deviceArgs}";
            // -a gives everything: identity + SMART attributes
            var raw = LinuxCommandRunner.TryRun(_smartctlPath, $"-a {devicePath}{args}");
            if (string.IsNullOrWhiteSpace(raw)) return null;

            if (raw.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
                raw.Contains("requires root", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var model = ParseField(raw, "Device Model")
                     ?? ParseField(raw, "Model Number")
                     ?? ParseField(raw, "Model Family")
                     ?? devicePath;

            var serial = ParseField(raw, "Serial Number") ?? "";

            // Health: SMART overall-health self-assessment
            bool healthPassed = raw.Contains("SMART overall-health self-assessment test result: PASSED",
                                    StringComparison.OrdinalIgnoreCase)
                             || raw.Contains(": PASSED", StringComparison.OrdinalIgnoreCase);

            // Health score from Reallocated_Sector_Ct and other key attributes
            int healthScore = CalculateHealthScore(raw, healthPassed);

            // Temperature — attribute 190 or 194
            int? temperature = ParseSmartAttribute(raw, "190")
                            ?? ParseSmartAttribute(raw, "194")
                            ?? ParseSmartAttribute(raw, "Temperature_Celsius");

            // Power-on hours — attribute 9
            int? powerOnHours = ParseSmartAttribute(raw, "9")
                             ?? ParseSmartAttribute(raw, "Power_On_Hours");

            var warnings = new List<string>();
            if (!healthPassed) warnings.Add("SMART health check failed");
            if (temperature > 60) warnings.Add($"High temperature: {temperature}°C");

            return new SmartDriveInfo
            {
                DevicePath    = devicePath,
                DeviceType    = string.IsNullOrWhiteSpace(deviceArgs) ? "" : deviceArgs.Replace("-d ", "").Trim(),
                Model         = model,
                SerialNumber  = serial,
                HealthScore   = healthScore,
                HealthPassed  = healthPassed,
                Temperature   = temperature,
                PowerOnHours  = powerOnHours,
                Warnings      = warnings
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// Derives a 0-100 health score from reallocated sectors, pending sectors,
    /// and uncorrectable errors — the three main SMART failure predictors.
    /// </summary>
    private static int CalculateHealthScore(string raw, bool healthPassed)
    {
        if (!healthPassed) return 15;

        int score = 100;

        // Reallocated Sector Count (attr 5)
        var reallocated = ParseSmartAttributeFromOutput(raw, "5", "Reallocated_Sector_Ct");
        if (reallocated.HasValue && reallocated.Value > 0)
            score -= Math.Min(40, reallocated.Value * 4);

        // Current Pending Sector (attr 197)
        var pending = ParseSmartAttributeFromOutput(raw, "197", "Current_Pending_Sector");
        if (pending.HasValue && pending.Value > 0)
            score -= Math.Min(30, pending.Value * 3);

        // Offline Uncorrectable (attr 198)
        var uncorrectable = ParseSmartAttributeFromOutput(raw, "198", "Offline_Uncorrectable");
        if (uncorrectable.HasValue && uncorrectable.Value > 0)
            score -= Math.Min(25, uncorrectable.Value * 5);

        return Math.Clamp(score, 0, 100);
    }

    /// <summary>
    /// Parses a SMART attribute RAW_VALUE by attribute ID or name.
    /// smartctl -a output format (ATA):
    ///   ID# ATTRIBUTE_NAME          FLAG     VALUE WORST THRESH TYPE      UPDATED  WHEN_FAILED RAW_VALUE
    ///     5 Reallocated_Sector_Ct   0x0033   100   100   036    Pre-fail  Always       -       0
    /// </summary>
    private static int? ParseSmartAttributeFromOutput(string raw, string attrId, string attrName)
    {
        // Match by ID
        var byId = Regex.Match(raw, $@"^\s*{attrId}\s+\S+\s+\S+\s+\d+\s+\d+\s+\d+\s+\S+\s+\S+\s+\S+\s+(\d+)",
            RegexOptions.Multiline);
        if (byId.Success && int.TryParse(byId.Groups[1].Value, out int v1)) return v1;

        // Match by name
        var byName = Regex.Match(raw, $@"{attrName}\s+\S+\s+\d+\s+\d+\s+\d+\s+\S+\s+\S+\s+\S+\s+(\d+)",
            RegexOptions.Multiline);
        if (byName.Success && int.TryParse(byName.Groups[1].Value, out int v2)) return v2;

        return null;
    }

    /// <summary>Parses a SMART attribute normalized VALUE (column 4) by ID or name.</summary>
    private static int? ParseSmartAttribute(string raw, string attrIdOrName)
    {
        // Try numeric ID match (temperature attributes store value in RAW_VALUE col)
        var m = Regex.Match(raw, $@"^\s*{Regex.Escape(attrIdOrName)}\s+\S+\s+\S+\s+(\d+)",
            RegexOptions.Multiline);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int v)) return v;

        // NVMe format: "Temperature:  XX Celsius"
        var nvme = Regex.Match(raw, @"Temperature:\s+(\d+)\s+Celsius", RegexOptions.IgnoreCase);
        if (nvme.Success && int.TryParse(nvme.Groups[1].Value, out int t)) return t;

        return null;
    }

    private static string? ParseField(string raw, string field)
    {
        var m = Regex.Match(raw, $@"^{Regex.Escape(field)}:\s*(.+)$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string ExtractSmartError(string output)
    {
        if (output.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            || output.Contains("requires admin", StringComparison.OrdinalIgnoreCase))
            return "Self-test requires sudo/root";

        if (output.Contains("not supported", StringComparison.OrdinalIgnoreCase))
            return "Self-test not supported by this drive";

        if (output.Contains("Unknown USB", StringComparison.OrdinalIgnoreCase))
            return "USB bridge — self-test not available";

        // Find the first line that looks like the actual error
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                          .Select(l => l.Trim())
                          .Where(l => !l.StartsWith("smartctl", StringComparison.OrdinalIgnoreCase) && 
                                      !l.StartsWith("Copyright", StringComparison.OrdinalIgnoreCase) &&
                                      !l.StartsWith("===", StringComparison.OrdinalIgnoreCase) &&
                                      !l.StartsWith("Sending command", StringComparison.OrdinalIgnoreCase) &&
                                      !string.IsNullOrWhiteSpace(l));
                          
        var errorLine = lines.FirstOrDefault(l => l.Contains("failed", StringComparison.OrdinalIgnoreCase) || 
                                                  l.Contains("error", StringComparison.OrdinalIgnoreCase)) 
                        ?? lines.FirstOrDefault();

        return errorLine ?? "Failed to start self-test";
    }

    private bool IsHardwareRaidPresent()
    {
        try
        {
            var lsblk = LinuxCommandRunner.TryRun("lsblk", "-J -o NAME,MODEL");
            return lsblk.Contains("PERC", StringComparison.OrdinalIgnoreCase) ||
                   lsblk.Contains("MegaRAID", StringComparison.OrdinalIgnoreCase) ||
                   lsblk.Contains("LOGICAL VOLUME", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private List<SmartDriveInfo> ProbeHardwareRaidDevices()
    {
        var devices = new List<SmartDriveInfo>();
        var virtualDisks = new[] { "/dev/sda", "/dev/sdb", "/dev/sdc" };

        foreach (var vd in virtualDisks)
        {
            if (!File.Exists(vd)) continue;

            // Common RAID controllers: megaraid (LSI/PERC), cciss (HP), areca
            var controllerTypes = new[] { "megaraid", "cciss" };
            
            foreach (var ctype in controllerTypes)
            {
                // Probe slots 0 to 15
                for (int i = 0; i < 16; i++)
                {
                    var devArgs = $"-d {ctype},{i}";
                    var info = BuildDriveInfo(vd, devArgs);
                    if (info != null)
                    {
                        // Ensure we aren't adding the virtual controller metadata as a real drive
                        if (!string.IsNullOrWhiteSpace(info.Model) && 
                            !info.Model.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                        {
                            devices.Add(info);
                        }
                    }
                    else if (i > 3)
                    {
                        // Optimization: if first 4 slots are empty, stop probing this controller type
                        break; 
                    }
                }
            }
        }

        return devices;
    }
}
