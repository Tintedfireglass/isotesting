using System.Reflection;
using System.Text.Json;
using LaptopQC.Core.Models;
using LaptopQC.Core.Services;
using Pramaan.CLI.Diagnostics;

namespace Pramaan.CLI.Agent;

public static class AgentCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        // Usage:
        // pramaan agent enroll --license XXXX --serial SERIAL [--api https://.../api]
        // pramaan agent check [--json] [--config path]
        // pramaan agent push [--config path]
        // pramaan agent heartbeat [--config path]
        var sub = args.Skip(1).FirstOrDefault()?.ToLowerInvariant();
        var flags = ParseFlags(args.Skip(2).ToArray());

        var configPath = flags.GetValueOrDefault("config");
        var config = AgentConfig.Load(configPath);

        if (flags.TryGetValue("api", out var apiUrl) && !string.IsNullOrWhiteSpace(apiUrl))
            config.ApiUrl = apiUrl.Trim();

        if (sub == "enroll")
        {
            if (flags.TryGetValue("license", out var license) && !string.IsNullOrWhiteSpace(license))
                config.LicenseKey = license.Trim();
            if (flags.TryGetValue("serial", out var serial) && !string.IsNullOrWhiteSpace(serial))
                config.MachineSerial = serial.Trim();

            // Best-effort auto-fill optional identity fields
            try
            {
                var sys = new LinuxSystemDiagnostic().GetInfo();
                config.ComputerName ??= sys.ComputerName;
                config.MacAddress ??= sys.MacAddress;
            }
            catch { }

            config.Save(configPath);

            // Verify auth now
            var auth = new AuthService(config.ApiUrl);
            var login = await auth.LoginWithLicenseAsync(config.LicenseKey ?? "", config.MachineSerial ?? "", config.MacAddress, config.ComputerName);
            if (!login.Success)
            {
                Console.Error.WriteLine(login.Message);
                return 3;
            }
            Console.WriteLine($"Enrolled. machineId={auth.MachineId}");
            return 0;
        }

        if (sub == "heartbeat")
        {
            if (string.IsNullOrWhiteSpace(config.LicenseKey) || string.IsNullOrWhiteSpace(config.MachineSerial))
            {
                Console.Error.WriteLine("Not enrolled. Run: pramaan agent enroll --license ... --serial ...");
                return 3;
            }
            var auth = new AuthService(config.ApiUrl);
            var login = await auth.LoginWithLicenseAsync(config.LicenseKey!, config.MachineSerial!, config.MacAddress, config.ComputerName);
            if (!login.Success)
            {
                Console.Error.WriteLine(login.Message);
                return 3;
            }
            Console.WriteLine("Heartbeat OK (license refresh).");
            return 0;
        }

        if (sub == "check" || sub == "push")
        {
            var agentVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString();
            var report = new LinuxServerHealthCollector(config).Collect(agentVersion);

            var jsonOut = flags.ContainsKey("json");
            if (jsonOut || sub == "push")
            {
                var json = JsonSerializer.Serialize(new
                {
                    schema_version = report.SchemaVersion,
                    collected_at = report.CollectedAtUtc,
                    agent_version = report.AgentVersion,
                    overall_status = report.OverallStatus.ToString().ToLowerInvariant(),
                    checks = report.Checks.Select(c => new
                    {
                        name = c.Name,
                        status = c.Status.ToString().ToLowerInvariant(),
                        summary = c.Summary,
                        metrics = c.Metrics,
                        details = c.Details
                    }).ToList()
                }, new JsonSerializerOptions { WriteIndented = true });
                Console.WriteLine(json);
            }
            else
            {
                Console.WriteLine($"Status: {report.OverallStatus}");
                foreach (var c in report.Checks)
                    Console.WriteLine($"- {c.Name}: {c.Status} {c.Summary}");
            }

            if (sub == "push")
            {
                if (string.IsNullOrWhiteSpace(config.LicenseKey) || string.IsNullOrWhiteSpace(config.MachineSerial))
                {
                    Console.Error.WriteLine("Not enrolled. Run: pramaan agent enroll --license ... --serial ...");
                    return 3;
                }

                var auth = new AuthService(config.ApiUrl);
                var login = await auth.LoginWithLicenseAsync(config.LicenseKey!, config.MachineSerial!, config.MacAddress, config.ComputerName);
                if (!login.Success)
                {
                    Console.Error.WriteLine(login.Message);
                    return 3;
                }

                var req = new SubmitServerHealthRequest
                {
                    SchemaVersion = report.SchemaVersion,
                    MachineId = auth.MachineId,
                    CollectedAt = report.CollectedAtUtc,
                    AgentVersion = report.AgentVersion,
                    OverallStatus = report.OverallStatus.ToString().ToLowerInvariant(),
                    Checks = report.Checks.Select(c => new ServerHealthCheckResult
                    {
                        Name = c.Name,
                        Status = c.Status.ToString().ToLowerInvariant(),
                        Summary = c.Summary,
                        Metrics = c.Metrics,
                        Details = c.Details
                    }).ToList()
                };

                var submitter = new ServerHealthSubmissionService(new ApiConfiguration { ApiUrl = config.ApiUrl });
                var res = await submitter.SubmitAsync(req, auth.Token);
                if (!res.Success)
                {
                    Console.Error.WriteLine(res.ErrorMessage ?? "Push failed");
                    return res.IsAuthError ? 2 : 3;
                }
                Console.WriteLine("Push OK.");
            }

            return StatusToExitCode(report.OverallStatus);
        }

        Console.Error.WriteLine("Usage: pramaan agent <enroll|check|push|heartbeat> [--config path] [--api url]");
        return 3;
    }

    private static int StatusToExitCode(ServerHealthStatus status) => status switch
    {
        ServerHealthStatus.Ok => 0,
        ServerHealthStatus.Degraded => 1,
        ServerHealthStatus.Critical => 2,
        _ => 3
    };

    private static Dictionary<string, string> ParseFlags(string[] args)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal)) continue;
            var key = a.Substring(2);
            var next = (i + 1) < args.Length ? args[i + 1] : null;
            if (next == null || next.StartsWith("--", StringComparison.Ordinal))
            {
                dict[key] = "true";
            }
            else
            {
                dict[key] = next;
                i++;
            }
        }
        return dict;
    }
}

