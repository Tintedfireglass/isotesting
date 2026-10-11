#if WINDOWS
using LaptopQC.Core.Diagnostics;
using LaptopQC.Hardware.Providers;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Management;
using System.Text.Json;

namespace LaptopQC.Core.Services;

/// <summary>
/// Assesses RAID array health using four independent layers:
///   Layer 1 – Windows Storage Spaces WMI (MSFT_VirtualDisk / MSFT_PhysicalDisk)
///   Layer 2a – storcli64 CLI (MegaRAID / Dell PERC / Broadcom)
///   Layer 2b – ssacli CLI (HP Smart Array / ProLiant)
///   Layer 3  – smartctl RAID passthrough (read-only SMART from member drives)
///   Layer 4  – Windows Event Log disk error scan (always runs as safety net)
///
/// Non-RAID machines: DetectAndAssess() returns IsRaidDetected=false quickly and
/// none of the RAID-specific paths are taken. Existing storage tests are unchanged.
/// </summary>
public class RaidHealthService
{
    private readonly ISmartctlProvider _smartctl;

    // ─── vendor CLI search paths ───────────────────────────────────────────────
    private static readonly string[] StorCLIPaths =
    {
        @"C:\Program Files\MegaRAID Storage Manager\storcli64.exe",
        @"C:\Program Files\Broadcom\storcli\storcli64.exe",
        @"C:\Program Files (x86)\MegaRAID Storage Manager\storcli64.exe",
        @"C:\Windows\System32\storcli64.exe",
        // Also check next to the app
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "storcli64.exe"),
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "storcli64.exe"),
    };

    private static readonly string[] SsaCLIPaths =
    {
        @"C:\Program Files\Smart Storage Administrator\ssacli\bin\ssacli.exe",
        @"C:\Program Files (x86)\Compaq\Hpacucli\Bin\hpacucli.exe",
        @"C:\Program Files\HP\hpssacli\bin\hpssacli.exe",
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "ssacli.exe"),
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ssacli.exe"),
    };

    public RaidHealthService(ISmartctlProvider? smartctl = null)
    {
        _smartctl = smartctl ?? new SmartctlProvider();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Public entry point
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Runs all RAID detection and health layers. On non-RAID machines this
    /// returns immediately with IsRaidDetected=false. No exceptions escape.
    /// </summary>
    public RaidHealthResult DetectAndAssess()
    {
        var result = new RaidHealthResult();
        try
        {
            // Layer 1 – Storage Spaces (quickest, always try first)
            TryStorageSpaces(result);

            // Layer 2a – storcli64 (MegaRAID / PERC / Broadcom)
            if (!result.IsRaidDetected || result.NeedsMoreInfo)
                TryStorCli(result);

            // Layer 2b – ssacli (HP Smart Array)
            if (!result.IsRaidDetected || result.NeedsMoreInfo)
                TrySsaCli(result);

            // Layer 3 – smartctl RAID passthrough (read-only SMART on member drives)
            // Only attempt if we know the controller type, or as a last-resort probe
            if (result.IsRaidDetected)
                TrySmartPassthrough(result);

            // Layer 4 – Windows Event Log (always runs as safety net)
            ScanEventLog(result);

            // Consolidate overall health
            if (result.IsRaidDetected)
                FinaliseHealth(result);
        }
        catch
        {
            // Never let RAID detection crash the main test pipeline
        }
        return result;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Layer 1 — Windows Storage Spaces (MSFT_VirtualDisk / MSFT_PhysicalDisk)
    // ══════════════════════════════════════════════════════════════════════════

    private static void TryStorageSpaces(RaidHealthResult result)
    {
        try
        {
            var scope = new ManagementScope(@"\\localhost\ROOT\Microsoft\Windows\Storage");
            scope.Connect();

            // Query virtual disks (the RAID volumes presented to the OS)
            var vdQuery = new ObjectQuery("SELECT * FROM MSFT_VirtualDisk");
            using var vdSearcher = new ManagementObjectSearcher(scope, vdQuery);
            var vdObjects = vdSearcher.Get();

            foreach (ManagementObject vd in vdObjects)
            {
                result.IsRaidDetected = true;
                result.ControllerType = "storage-spaces";

                var array = new RaidArrayInfo
                {
                    ControllerType = "storage-spaces",
                    Name = vd["FriendlyName"]?.ToString() ?? "Storage Space",
                    Level = MapStorageSpacesLayout(vd["ResiliencySettingName"]?.ToString()),
                    TotalSizeGB = vd["Size"] is ulong size ? size / (1024.0 * 1024 * 1024) : 0,
                };

                // HealthStatus: 0=Healthy, 1=Warning, 2=Unhealthy
                int health = vd["HealthStatus"] is ushort hs ? hs : 0;
                array.IsHealthy = health == 0;
                array.HealthStatus = health switch
                {
                    0 => "Healthy",
                    1 => "Warning",
                    2 => "Unhealthy",
                    _ => "Unknown"
                };
                array.State = array.HealthStatus;

                result.Arrays.Add(array);
                result.Details.Add($"[Storage Spaces] Virtual Disk '{array.Name}': {array.HealthStatus} ({array.Level})");
            }

            if (!result.IsRaidDetected) return;

            // Query physical disks in the storage pool
            var pdQuery = new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk");
            using var pdSearcher = new ManagementObjectSearcher(scope, pdQuery);

            foreach (ManagementObject pd in pdSearcher.Get())
            {
                // Usage: 0=Unknown,1=Auto-Select,2=Manual Select,3=Hot Spare,4=Retired,5=Journal
                var usage = pd["Usage"] is ushort u ? u : 0;
                if (usage == 0 || usage == 4) continue; // skip unassigned / retired

                int pdHealth = pd["HealthStatus"] is ushort ph ? ph : 0;
                bool pdHealthy = pdHealth == 0;
                string pdName = pd["FriendlyName"]?.ToString() ?? "Unknown";
                string pdState = pdHealth switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" };
                double pdSizeGb = pd["Size"] is ulong ps ? ps / (1024.0 * 1024 * 1024) : 0;

                var member = new RaidMemberDriveInfo
                {
                    Location = pdName,
                    State = pdState,
                    IsHealthy = pdHealthy,
                    SizeGB = pdSizeGb,
                };
                result.MemberDrives.Add(member);

                if (!pdHealthy)
                    result.Warnings.Add($"Physical disk '{pdName}' health: {pdState}");
            }

            // Count active vs total from member list
            if (result.Arrays.Count > 0 && result.MemberDrives.Count > 0)
            {
                result.Arrays[0].TotalDrives = result.MemberDrives.Count;
                result.Arrays[0].ActiveDrives = result.MemberDrives.Count(m => m.IsHealthy);
            }
        }
        catch
        {
            // Storage Spaces WMI not available (not configured or old Windows version)
        }
    }

    private static string MapStorageSpacesLayout(string? resiliencyName) => resiliencyName?.ToLowerInvariant() switch
    {
        "mirror" => "Mirror (RAID 1)",
        "parity" => "Parity (RAID 5/6)",
        "simple" => "Simple (RAID 0)",
        "three-way mirror" => "Three-Way Mirror (RAID 1E)",
        _ => resiliencyName ?? "Unknown RAID level"
    };

    // ══════════════════════════════════════════════════════════════════════════
    // Layer 2a — storcli64 (MegaRAID / Dell PERC / Broadcom)
    // ══════════════════════════════════════════════════════════════════════════

    private static void TryStorCli(RaidHealthResult result)
    {
        var storCliPath = StorCLIPaths.FirstOrDefault(File.Exists);
        if (storCliPath == null)
        {
            // Also check PATH
            storCliPath = FindInPath("storcli64.exe") ?? FindInPath("StorCLI.exe");
        }
        if (storCliPath == null) return;

        try
        {
            // Virtual drives (arrays)
            var vdOutput = RunCli(storCliPath, "/call/vall show all J");
            if (!string.IsNullOrWhiteSpace(vdOutput))
                ParseStorCliVirtualDrives(vdOutput, result);

            // Physical drives (members)
            var pdOutput = RunCli(storCliPath, "/call/eall/sall show all J");
            if (!string.IsNullOrWhiteSpace(pdOutput))
                ParseStorCliPhysicalDrives(pdOutput, result);
        }
        catch { /* storcli not accessible */ }
    }

    private static void ParseStorCliVirtualDrives(string json, RaidHealthResult result)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // storcli JSON: Controllers[].Response Data."VD LIST"[].State / TYPE / Size
            if (!root.TryGetProperty("Controllers", out var controllers)) return;

            foreach (var ctrl in controllers.EnumerateArray())
            {
                if (!ctrl.TryGetProperty("Response Data", out var rd)) continue;
                if (!rd.TryGetProperty("VD LIST", out var vdList)) continue;

                foreach (var vd in vdList.EnumerateArray())
                {
                    result.IsRaidDetected = true;
                    result.ControllerType = "megaraid";

                    string state = vd.TryGetProperty("State", out var s) ? s.GetString() ?? "" : "";
                    string type = vd.TryGetProperty("TYPE", out var t) ? t.GetString() ?? "" : "";
                    string dgVd = vd.TryGetProperty("DG/VD", out var dv) ? dv.GetString() ?? "" : "";
                    string sizeProp = vd.TryGetProperty("Size", out var sz) ? sz.GetString() ?? "" : "";

                    // State: Optl=Optimal, Dgrd=Degraded, Pdgd=Partially Degraded, Offln=Offline
                    bool healthy = state.Equals("Optl", StringComparison.OrdinalIgnoreCase);
                    string humanState = state switch
                    {
                        "Optl" => "Optimal",
                        "Dgrd" => "Degraded",
                        "Pdgd" => "Partially Degraded",
                        "Offln" => "Offline/Failed",
                        _ => state
                    };

                    var array = new RaidArrayInfo
                    {
                        ControllerType = "megaraid",
                        Name = $"VD {dgVd}",
                        Level = MapMegaRaidType(type),
                        State = humanState,
                        IsHealthy = healthy,
                        HealthStatus = humanState,
                    };
                    result.Arrays.Add(array);
                    result.Details.Add($"[MegaRAID] Virtual Drive {dgVd} ({array.Level}): {humanState}");

                    if (!healthy)
                        result.Warnings.Add($"Virtual Drive {dgVd} is {humanState}");
                }
            }
        }
        catch { /* JSON parse failure — storcli version mismatch */ }
    }

    private static void ParseStorCliPhysicalDrives(string json, RaidHealthResult result)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("Controllers", out var controllers)) return;

            foreach (var ctrl in controllers.EnumerateArray())
            {
                if (!ctrl.TryGetProperty("Response Data", out var rd)) continue;
                if (!rd.TryGetProperty("PD LIST", out var pdList)) continue;

                foreach (var pd in pdList.EnumerateArray())
                {
                    string eidSlt = pd.TryGetProperty("EID:Slt", out var es) ? es.GetString() ?? "" : "";
                    string state = pd.TryGetProperty("State", out var s) ? s.GetString() ?? "" : "";
                    long medErr = pd.TryGetProperty("Med Err", out var me) ? me.GetInt64() : 0;
                    bool predFail = pd.TryGetProperty("Pred Fail", out var pf) && pf.GetString()?.Equals("Y", StringComparison.OrdinalIgnoreCase) == true;

                    // State: Onln=Online, Failed=Failed, Rbld=Rebuilding, UGood=Unconfigured Good
                    bool healthy = state.Equals("Onln", StringComparison.OrdinalIgnoreCase) ||
                                   state.Equals("Rbld", StringComparison.OrdinalIgnoreCase) ||
                                   state.Equals("UGood", StringComparison.OrdinalIgnoreCase);
                    string humanState = state switch
                    {
                        "Onln" => "Online",
                        "Failed" => "Failed",
                        "Rbld" => "Rebuilding",
                        "UGood" => "Unconfigured Good",
                        "UBad" => "Unconfigured Bad",
                        _ => state
                    };

                    var member = new RaidMemberDriveInfo
                    {
                        Location = $"Slot {eidSlt}",
                        State = humanState,
                        IsHealthy = healthy,
                        MediaErrors = medErr,
                        PredictiveFail = predFail,
                    };
                    result.MemberDrives.Add(member);

                    if (!healthy)
                        result.Warnings.Add($"Physical drive {eidSlt} is {humanState}");
                    if (predFail)
                        result.Warnings.Add($"Physical drive {eidSlt}: predictive failure flag set");
                    if (medErr > 0)
                        result.Warnings.Add($"Physical drive {eidSlt}: {medErr} media error(s)");
                }
            }

            // Back-fill active/total drive counts on arrays
            foreach (var arr in result.Arrays)
            {
                arr.TotalDrives = result.MemberDrives.Count;
                arr.ActiveDrives = result.MemberDrives.Count(m => m.IsHealthy);
            }
        }
        catch { }
    }

    private static string MapMegaRaidType(string? type) => (type ?? "").ToUpperInvariant() switch
    {
        "RAID0" or "R0" => "RAID 0 (Stripe)",
        "RAID1" or "R1" => "RAID 1 (Mirror)",
        "RAID5" or "R5" => "RAID 5 (Stripe+Parity)",
        "RAID6" or "R6" => "RAID 6 (Double Parity)",
        "RAID10" or "R10" => "RAID 10 (Mirror+Stripe)",
        _ => type ?? "RAID"
    };

    // ══════════════════════════════════════════════════════════════════════════
    // Layer 2b — ssacli (HP Smart Array / ProLiant)
    // ══════════════════════════════════════════════════════════════════════════

    private static void TrySsaCli(RaidHealthResult result)
    {
        var ssaPath = SsaCLIPaths.FirstOrDefault(File.Exists)
                   ?? FindInPath("ssacli.exe")
                   ?? FindInPath("hpssacli.exe");
        if (ssaPath == null) return;

        try
        {
            // Discover controller slots
            var ctrlOutput = RunCli(ssaPath, "ctrl all show");
            if (string.IsNullOrWhiteSpace(ctrlOutput)) return;

            var slots = ParseHpControllerSlots(ctrlOutput);
            if (slots.Count == 0) return;

            result.IsRaidDetected = true;
            result.ControllerType = "hp-smart-array";

            foreach (var slot in slots)
            {
                // Logical drives
                var ldOutput = RunCli(ssaPath, $"ctrl slot={slot} ld all show status");
                if (!string.IsNullOrWhiteSpace(ldOutput))
                    ParseHpLogicalDrives(ldOutput, slot, result);

                // Physical drives
                var pdOutput = RunCli(ssaPath, $"ctrl slot={slot} pd all show status");
                if (!string.IsNullOrWhiteSpace(pdOutput))
                    ParseHpPhysicalDrives(pdOutput, result);
            }
        }
        catch { }
    }

    private static List<string> ParseHpControllerSlots(string output)
    {
        var slots = new List<string>();
        // Pattern: "Smart Array P440ar in Slot 0" or "Smart Array ... in Slot 1 (Embedded)"
        foreach (var line in output.Split('\n'))
        {
            var lower = line.ToLowerInvariant();
            if (!lower.Contains("slot")) continue;
            var idx = lower.IndexOf("slot");
            if (idx < 0) continue;
            var afterSlot = line.Substring(idx + 4).Trim();
            var slotNum = new string(afterSlot.TakeWhile(c => char.IsDigit(c) || c == ' ').ToArray()).Trim();
            if (!string.IsNullOrEmpty(slotNum) && !slots.Contains(slotNum))
                slots.Add(slotNum);
        }
        return slots;
    }

    private static void ParseHpLogicalDrives(string output, string slot, RaidHealthResult result)
    {
        // Pattern: "logicaldrive 1 (279.4 GB, RAID 5, OK)"
        foreach (var line in output.Split('\n'))
        {
            var lower = line.ToLowerInvariant();
            if (!lower.Contains("logicaldrive") && !lower.Contains("logical drive")) continue;

            bool healthy = lower.Contains(": ok") || lower.EndsWith("ok)") || lower.Contains(", ok,") || lower.Contains(", ok)");
            bool degraded = lower.Contains("degraded");
            bool failed = lower.Contains("failed");
            string state = failed ? "Failed" : degraded ? "Degraded" : "OK";

            var array = new RaidArrayInfo
            {
                ControllerType = "hp-smart-array",
                Name = $"Slot {slot} LD",
                Level = ExtractHpRaidLevel(line),
                State = state,
                IsHealthy = healthy,
                HealthStatus = state,
            };
            result.Arrays.Add(array);
            result.Details.Add($"[HP Smart Array] Slot {slot} Logical Drive: {state}");

            if (!healthy)
                result.Warnings.Add($"HP Smart Array Slot {slot} logical drive is {state}");
        }
    }

    private static string ExtractHpRaidLevel(string line)
    {
        // e.g. "RAID 5" or "RAID5" or "RAID 1+0"
        var lower = line.ToLowerInvariant();
        var idx = lower.IndexOf("raid");
        if (idx < 0) return "RAID";
        return line.Substring(idx, Math.Min(10, line.Length - idx)).Trim().TrimEnd(',', ')');
    }

    private static void ParseHpPhysicalDrives(string output, RaidHealthResult result)
    {
        // Pattern: "physicaldrive 1I:1:1 (port 1I:box 1:bay 1, 900 GB): OK"
        foreach (var line in output.Split('\n'))
        {
            var lower = line.ToLowerInvariant();
            if (!lower.Contains("physicaldrive") && !lower.Contains("physical drive")) continue;

            bool healthy = lower.Contains(": ok") || lower.EndsWith("ok");
            bool failed = lower.Contains("failed");
            bool rebuilding = lower.Contains("rebuilding");
            string state = failed ? "Failed" : rebuilding ? "Rebuilding" : healthy ? "OK" : "Unknown";

            // Extract location token (everything before the first space after "physicaldrive")
            var locStart = lower.IndexOf("physicaldrive");
            string location = "Unknown";
            if (locStart >= 0)
            {
                var afterKeyword = line.Substring(locStart + "physicaldrive".Length).TrimStart();
                location = afterKeyword.Split(' ')[0];
            }

            var member = new RaidMemberDriveInfo
            {
                Location = location,
                State = state,
                IsHealthy = healthy && !failed,
            };
            result.MemberDrives.Add(member);

            if (!member.IsHealthy)
                result.Warnings.Add($"Physical drive {location} is {state}");
        }

        // Update active/total counts
        foreach (var arr in result.Arrays.Where(a => a.ControllerType == "hp-smart-array"))
        {
            arr.TotalDrives = result.MemberDrives.Count;
            arr.ActiveDrives = result.MemberDrives.Count(m => m.IsHealthy);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Layer 3 — smartctl RAID Passthrough (read-only, no self-test)
    // ══════════════════════════════════════════════════════════════════════════

    private void TrySmartPassthrough(RaidHealthResult result)
    {
        if (!_smartctl.IsAvailable) return;

        // Determine which passthrough types to try based on detected controller
        var passthroughTypes = GetPassthroughTypes(result.ControllerType);
        if (passthroughTypes.Count == 0) return;

        int driveIndex = 0;
        foreach (var (basePath, typePrefix) in passthroughTypes)
        {
            int consecutiveFails = 0;
            for (int i = 0; i < 16; i++)
            {
                string driveType = $"{typePrefix},{i}";
                var smartData = _smartctl.GetSmartData(basePath, driveType);
                if (smartData == null)
                {
                    consecutiveFails++;
                    if (consecutiveFails >= 2) break; // stop probing this type
                    continue;
                }
                consecutiveFails = 0;

                int score = smartData.CalculateHealthScore();
                bool healthy = smartData.HealthPassed && score >= 50;

                // Find matching member drive entry and enrich it, or add new
                var existing = result.MemberDrives.FirstOrDefault(m =>
                    m.SmartHealthScore == null && m.Location.Contains(i.ToString()));

                if (existing != null)
                {
                    existing.SmartHealthScore = score;
                    existing.IsHealthy = existing.IsHealthy && healthy;
                }
                else
                {
                    result.MemberDrives.Add(new RaidMemberDriveInfo
                    {
                        Location = $"Drive {i} ({driveType})",
                        State = healthy ? "Online" : "Degraded",
                        IsHealthy = healthy,
                        SmartHealthScore = score,
                        MediaErrors = smartData.NvmeMediaErrors ?? 0,
                    });
                }

                if (!string.IsNullOrWhiteSpace(smartData.Model))
                    result.Details.Add($"[SMART Passthrough] {smartData.Model} ({driveType}): Health {score}%");

                driveIndex++;
            }
        }

        if (driveIndex > 0)
            result.Details.Add($"[SMART Passthrough] Read SMART data from {driveIndex} RAID member drive(s) (read-only, no self-test)");
    }

    private static List<(string BasePath, string TypePrefix)> GetPassthroughTypes(string controllerType)
    {
        return controllerType switch
        {
            "megaraid" => new List<(string, string)>
            {
                ("/dev/sda", "megaraid"),
                ("/dev/sda", "sat+megaraid"),
            },
            "hp-smart-array" => new List<(string, string)>
            {
                ("/dev/sda", "cciss"),
            },
            "intel-rst" => new List<(string, string)>
            {
                ("/dev/pd0", "sat"),    // Intel RST: /dev/pd0..pdN
            },
            _ => new List<(string, string)>
            {
                ("/dev/sda", "megaraid"),
                ("/dev/sda", "cciss"),
            }
        };
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Layer 4 — Windows Event Log Disk Error Scan (always runs)
    // ══════════════════════════════════════════════════════════════════════════

    private static void ScanEventLog(RaidHealthResult result)
    {
        try
        {
            // Scan the last 30 days for disk-related errors
            var since = DateTime.UtcNow.AddDays(-30);
            string sinceStr = since.ToString("o"); // ISO 8601

            // XPath targeting Event IDs 7 (bad block), 11 (controller error), 51 (paging/IO error)
            string query = $"*[System[Provider[@Name='disk' or @Name='storahci' or @Name='megasas' or @Name='hpsa']" +
                           $" and (EventID=7 or EventID=11 or EventID=51)" +
                           $" and TimeCreated[@SystemTime>='{sinceStr}']]]";

            var evtQuery = new EventLogQuery("System", PathType.LogName, query);
            using var reader = new EventLogReader(evtQuery);

            int count = 0;
            EventRecord? evt;
            while ((evt = reader.ReadEvent()) != null)
            {
                using (evt)
                    count++;
            }

            result.DiskErrorEventCount = count;

            if (count == 0)
                result.Details.Add("[Event Log] 0 disk error events in last 30 days");
            else if (count <= 5)
                result.Details.Add($"[Event Log] {count} disk error event(s) in last 30 days (minor)");
            else
            {
                result.Details.Add($"[Event Log] WARNING: {count} disk error events in last 30 days");
                result.Warnings.Add($"{count} disk error events detected in System log (Event IDs 7/11/51)");
            }
        }
        catch
        {
            result.Details.Add("[Event Log] Could not read event log (insufficient permissions or log unavailable)");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Health Finalisation
    // ══════════════════════════════════════════════════════════════════════════

    private static void FinaliseHealth(RaidHealthResult result)
    {
        bool anyArrayDegraded = result.Arrays.Any(a => !a.IsHealthy);
        bool anyMemberFailed = result.MemberDrives.Any(m => !m.IsHealthy && m.State.Equals("Failed", StringComparison.OrdinalIgnoreCase));
        bool anyPredictiveFail = result.MemberDrives.Any(m => m.PredictiveFail);
        bool manyDiskErrors = result.DiskErrorEventCount > 10;

        if (anyMemberFailed || anyArrayDegraded)
        {
            result.IsHealthy = false;
            result.HealthSummary = anyMemberFailed
                ? "RAID array degraded — one or more member drives have failed"
                : "RAID array is degraded";
        }
        else if (anyPredictiveFail || manyDiskErrors)
        {
            result.IsHealthy = true; // Still pass but with warnings
            result.HealthSummary = anyPredictiveFail
                ? "RAID healthy but predictive failure flag set on member drive(s)"
                : $"RAID healthy but elevated disk errors detected ({result.DiskErrorEventCount} in 30 days)";
        }
        else if (result.Arrays.Count > 0)
        {
            result.IsHealthy = true;
            var arr = result.Arrays[0];
            string driveCounts = arr.TotalDrives > 0 ? $", {arr.ActiveDrives}/{arr.TotalDrives} drives active" : "";
            result.HealthSummary = $"RAID array healthy — {arr.Level}{driveCounts}";
        }
        else
        {
            // RAID detected but couldn't get detailed array info
            result.IsHealthy = true;
            result.HealthSummary = "RAID detected — limited health data available (vendor CLI not found)";
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Utilities
    // ══════════════════════════════════════════════════════════════════════════

    private static string? FindInPath(string exeName)
    {
        try
        {
            var result = RunProcess("where", exeName);
            if (!string.IsNullOrWhiteSpace(result))
            {
                var first = result.Split('\n')[0].Trim();
                if (File.Exists(first)) return first;
            }
        }
        catch { }
        return null;
    }

    private static string RunCli(string exe, string args, int timeoutMs = 15000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return "";
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(timeoutMs);
            return output;
        }
        catch { return ""; }
    }

    private static string RunProcess(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return "";
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            return output;
        }
        catch { return ""; }
    }
}

// ══════════════════════════════════════════════════════════════════════════════
// Result Models
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Aggregated RAID health result from all detection layers.
/// IsRaidDetected=false means this is a non-RAID machine — callers should
/// ignore all RAID-specific fields in that case.
/// </summary>
public class RaidHealthResult
{
    public bool IsRaidDetected { get; set; }
    /// <summary>e.g. "storage-spaces", "megaraid", "hp-smart-array", "intel-rst"</summary>
    public string ControllerType { get; set; } = "";
    public bool IsHealthy { get; set; } = true;
    public string HealthSummary { get; set; } = "";
    public List<RaidArrayInfo> Arrays { get; set; } = new();
    public List<RaidMemberDriveInfo> MemberDrives { get; set; } = new();
    public int DiskErrorEventCount { get; set; }
    public List<string> Details { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    /// <summary>True when RAID is detected but we want to try another layer for richer data.</summary>
    internal bool NeedsMoreInfo => IsRaidDetected && Arrays.Count == 0;
}

public class RaidMemberDriveInfo
{
    public string Location { get; set; } = "";
    /// <summary>"Online", "Failed", "Rebuilding", "Healthy", "Warning", etc.</summary>
    public string State { get; set; } = "";
    public bool IsHealthy { get; set; } = true;
    public double SizeGB { get; set; }
    /// <summary>null when SMART passthrough is not available for this drive.</summary>
    public int? SmartHealthScore { get; set; }
    public long MediaErrors { get; set; }
    public bool PredictiveFail { get; set; }
}

#endif
