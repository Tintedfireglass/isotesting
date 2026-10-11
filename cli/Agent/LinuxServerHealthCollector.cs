using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Pramaan.CLI.Diagnostics;

namespace Pramaan.CLI.Agent;

public sealed class LinuxServerHealthCollector
{
    private readonly AgentConfig _config;

    public LinuxServerHealthCollector(AgentConfig config)
    {
        _config = config;
    }

    public ServerHealthReport Collect(string? agentVersion)
    {
        var report = new ServerHealthReport
        {
            AgentVersion = agentVersion,
            CollectedAtUtc = DateTime.UtcNow
        };

        report.Checks.Add(CheckIdentity());
        report.Checks.Add(CheckCpu());
        report.Checks.Add(CheckMemory());
        report.Checks.Add(CheckDisk());
        report.Checks.Add(CheckNetworkBasics());
        report.Checks.Add(CheckServices());
        report.Checks.Add(CheckTimeSync());

        report.OverallStatus = Aggregate(report.Checks);
        return report;
    }

    private static ServerHealthStatus Aggregate(IEnumerable<ServerHealthCheck> checks)
    {
        var statuses = checks.Select(c => c.Status).ToList();
        if (statuses.Contains(ServerHealthStatus.Critical)) return ServerHealthStatus.Critical;
        if (statuses.Contains(ServerHealthStatus.Degraded)) return ServerHealthStatus.Degraded;
        if (statuses.Contains(ServerHealthStatus.Unknown)) return ServerHealthStatus.Unknown;
        return ServerHealthStatus.Ok;
    }

    private ServerHealthCheck CheckIdentity()
    {
        try
        {
            var sys = new LinuxSystemDiagnostic().GetInfo();
            var uptime = LinuxCommandRunner.TryRun("uptime", "-p").Trim();
            return new ServerHealthCheck
            {
                Name = "identity",
                Status = ServerHealthStatus.Ok,
                Summary = $"{sys.ComputerName} | {sys.OsVersion}",
                Details = new List<string>
                {
                    $"hostname={sys.ComputerName}",
                    $"os={sys.OsVersion}",
                    string.IsNullOrWhiteSpace(uptime) ? "uptime=unknown" : $"uptime={uptime}",
                }
            };
        }
        catch (Exception ex)
        {
            return new ServerHealthCheck { Name = "identity", Status = ServerHealthStatus.Unknown, Summary = ex.Message };
        }
    }

    private ServerHealthCheck CheckCpu()
    {
        try
        {
            var loadText = LinuxCommandRunner.ReadFile("/proc/loadavg");
            var parts = loadText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            double.TryParse(parts.ElementAtOrDefault(0), out var load1);
            double.TryParse(parts.ElementAtOrDefault(1), out var load5);
            double.TryParse(parts.ElementAtOrDefault(2), out var load15);

            var cores = Environment.ProcessorCount <= 0 ? 1 : Environment.ProcessorCount;
            var warn = _config.LoadWarnPerCore * cores;
            var crit = _config.LoadCritPerCore * cores;

            var status = load1 >= crit ? ServerHealthStatus.Critical :
                         load1 >= warn ? ServerHealthStatus.Degraded :
                         ServerHealthStatus.Ok;

            return new ServerHealthCheck
            {
                Name = "cpu",
                Status = status,
                Summary = $"load1={load1:F2} load5={load5:F2} load15={load15:F2} (cores={cores})",
                Metrics = new Dictionary<string, double>
                {
                    ["load1"] = load1,
                    ["load5"] = load5,
                    ["load15"] = load15,
                    ["cores"] = cores
                }
            };
        }
        catch (Exception ex)
        {
            return new ServerHealthCheck { Name = "cpu", Status = ServerHealthStatus.Unknown, Summary = ex.Message };
        }
    }

    private ServerHealthCheck CheckMemory()
    {
        try
        {
            var meminfo = LinuxCommandRunner.ReadFile("/proc/meminfo");
            long totalKb = ParseMeminfoKb(meminfo, "MemTotal");
            long availKb = ParseMeminfoKb(meminfo, "MemAvailable");
            long swapTotalKb = ParseMeminfoKb(meminfo, "SwapTotal");
            long swapFreeKb = ParseMeminfoKb(meminfo, "SwapFree");

            double usedPct = totalKb > 0 ? (1.0 - (double)availKb / totalKb) * 100.0 : 0;
            double swapUsedPct = swapTotalKb > 0 ? (1.0 - (double)swapFreeKb / swapTotalKb) * 100.0 : 0;

            var status = usedPct >= _config.MemCritPercent ? ServerHealthStatus.Critical :
                         usedPct >= _config.MemWarnPercent ? ServerHealthStatus.Degraded :
                         ServerHealthStatus.Ok;

            return new ServerHealthCheck
            {
                Name = "memory",
                Status = status,
                Summary = $"mem_used={usedPct:F0}% swap_used={swapUsedPct:F0}%",
                Metrics = new Dictionary<string, double>
                {
                    ["mem_total_kb"] = totalKb,
                    ["mem_available_kb"] = availKb,
                    ["mem_used_pct"] = usedPct,
                    ["swap_total_kb"] = swapTotalKb,
                    ["swap_free_kb"] = swapFreeKb,
                    ["swap_used_pct"] = swapUsedPct
                }
            };
        }
        catch (Exception ex)
        {
            return new ServerHealthCheck { Name = "memory", Status = ServerHealthStatus.Unknown, Summary = ex.Message };
        }
    }

    private static long ParseMeminfoKb(string meminfo, string key)
    {
        var m = Regex.Match(meminfo, $@"^{Regex.Escape(key)}:\s+(\d+)\s+kB", RegexOptions.Multiline);
        return m.Success && long.TryParse(m.Groups[1].Value, out var kb) ? kb : 0;
    }

    private ServerHealthCheck CheckDisk()
    {
        try
        {
            var df = LinuxCommandRunner.TryRun("df", "-P -T");
            if (string.IsNullOrWhiteSpace(df))
                return new ServerHealthCheck { Name = "disk", Status = ServerHealthStatus.Unknown, Summary = "df output unavailable" };

            var lines = df.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1);
            double worstPct = 0;
            var details = new List<string>();

            foreach (var line in lines)
            {
                var cols = Regex.Split(line.Trim(), @"\s+");
                if (cols.Length < 7) continue;
                var fs = cols[0];
                var type = cols[1];
                var usePctText = cols[5].TrimEnd('%');
                var mount = cols[6];
                if (type is "tmpfs" or "devtmpfs") continue;
                if (!double.TryParse(usePctText, out var pct)) continue;
                worstPct = Math.Max(worstPct, pct);
                details.Add($"{mount} used={pct:F0}% fs={fs}");
            }

            var status = worstPct >= _config.DiskCritPercent ? ServerHealthStatus.Critical :
                         worstPct >= _config.DiskWarnPercent ? ServerHealthStatus.Degraded :
                         ServerHealthStatus.Ok;

            return new ServerHealthCheck
            {
                Name = "disk",
                Status = status,
                Summary = $"worst_used={worstPct:F0}%",
                Metrics = new Dictionary<string, double> { ["worst_used_pct"] = worstPct },
                Details = details.Take(40).ToList()
            };
        }
        catch (Exception ex)
        {
            return new ServerHealthCheck { Name = "disk", Status = ServerHealthStatus.Unknown, Summary = ex.Message };
        }
    }

    private ServerHealthCheck CheckNetworkBasics()
    {
        try
        {
            var details = new List<string>();
            var ipRoute = LinuxCommandRunner.TryRun("ip", "route");
            bool hasDefault = ipRoute.Split('\n').Any(l => l.StartsWith("default "));
            details.Add(hasDefault ? "default_route=present" : "default_route=missing");

            // DNS test (best-effort) via getent
            var dnsDomain = string.IsNullOrWhiteSpace(_config.DnsTestDomain) ? "google.com" : _config.DnsTestDomain;
            var getent = LinuxCommandRunner.TryRun("getent", $"hosts {dnsDomain}");
            bool dnsOk = !string.IsNullOrWhiteSpace(getent);
            details.Add(dnsOk ? $"dns_ok={dnsDomain}" : $"dns_fail={dnsDomain}");

            // Reachability URLs (best-effort) - TCP connect to host:port from URL
            int okCount = 0, total = 0;
            foreach (var url in _config.ReachabilityUrls ?? Array.Empty<string>())
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
                total++;
                var port = uri.Port > 0 ? uri.Port : (uri.Scheme == "https" ? 443 : 80);
                var ok = TryTcpConnect(uri.Host, port, timeoutMs: 1500);
                if (ok) okCount++;
                details.Add($"reach:{uri.Host}:{port}={(ok ? "ok" : "fail")}");
            }

            var status = (!hasDefault || !dnsOk) ? ServerHealthStatus.Degraded : ServerHealthStatus.Ok;
            if (total > 0 && okCount == 0) status = ServerHealthStatus.Degraded;

            return new ServerHealthCheck
            {
                Name = "network",
                Status = status,
                Summary = total > 0 ? $"default={hasDefault} dns={dnsOk} reach={okCount}/{total}" : $"default={hasDefault} dns={dnsOk}",
                Details = details
            };
        }
        catch (Exception ex)
        {
            return new ServerHealthCheck { Name = "network", Status = ServerHealthStatus.Unknown, Summary = ex.Message };
        }
    }

    private static bool TryTcpConnect(string host, int port, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            return task.Wait(timeoutMs) && client.Connected;
        }
        catch { return false; }
    }

    private ServerHealthCheck CheckServices()
    {
        try
        {
            var units = _config.ServiceUnits ?? Array.Empty<string>();
            if (units.Length == 0)
                return new ServerHealthCheck { Name = "services", Status = ServerHealthStatus.Ok, Summary = "no units configured" };

            var details = new List<string>();
            bool anyFailed = false;

            foreach (var unit in units.Where(u => !string.IsNullOrWhiteSpace(u)).Take(25))
            {
                var state = LinuxCommandRunner.RunSingleLine("systemctl", $"is-active {unit}");
                details.Add($"{unit}={state}");
                if (state.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
                    state.Equals("inactive", StringComparison.OrdinalIgnoreCase) ||
                    state.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                {
                    anyFailed = true;
                }
            }

            return new ServerHealthCheck
            {
                Name = "services",
                Status = anyFailed ? ServerHealthStatus.Critical : ServerHealthStatus.Ok,
                Summary = anyFailed ? "one or more units not active" : "all units active",
                Details = details
            };
        }
        catch (Exception ex)
        {
            return new ServerHealthCheck { Name = "services", Status = ServerHealthStatus.Unknown, Summary = ex.Message };
        }
    }

    private ServerHealthCheck CheckTimeSync()
    {
        try
        {
            // systemd-timesyncd / timedatectl is the simplest best-effort check.
            var statusOut = LinuxCommandRunner.TryRun("timedatectl", "show -p NTPSynchronized --value");
            var sync = statusOut.Trim();
            if (string.IsNullOrWhiteSpace(sync))
                return new ServerHealthCheck { Name = "time_sync", Status = ServerHealthStatus.Unknown, Summary = "timedatectl unavailable" };

            bool ok = sync.Equals("yes", StringComparison.OrdinalIgnoreCase) || sync.Equals("true", StringComparison.OrdinalIgnoreCase);
            return new ServerHealthCheck
            {
                Name = "time_sync",
                Status = ok ? ServerHealthStatus.Ok : ServerHealthStatus.Degraded,
                Summary = ok ? "ntp_synchronized=yes" : "ntp_synchronized=no"
            };
        }
        catch (Exception ex)
        {
            return new ServerHealthCheck { Name = "time_sync", Status = ServerHealthStatus.Unknown, Summary = ex.Message };
        }
    }
}

