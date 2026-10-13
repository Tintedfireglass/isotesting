using System.Net;
using System.Text.Json;
using LaptopQC.Core.Diagnostics;
using LaptopQC.Hardware.Models;
using Pramaan.CLI.Diagnostics;
using Pramaan.CLI.UI;

static class ActivationStorageChecks
{
    public static async Task RunAsync()
    {
        static void Check(bool ok, string error) { if (!ok) throw new Exception(error); }
        var handler = new ActivationHandler();
        var gate = new OnlineActivation(new HttpClient(handler), "https://test.invalid/api");
        var machine = new SystemInfo { SerialNumber = "ORIGINAL-123", ComputerName = "fixture" };
        foreach (bool trial in new[] { false, true })
        {
            handler.Body = JsonSerializer.Serialize(new { token = "verified", machineId = 123,
                user = new { id = trial ? 0 : 12 }, trialEndsAt = DateTime.UtcNow.AddDays(2) });
            handler.Code = HttpStatusCode.OK;
            var verified = await gate.VerifyAsync(trial, trial ? "fixture@example.test" : "license-fixture", machine);
            Check(verified.MachineId == 123 && verified.Token == "verified", "Verified activation lost identity");
            Check(handler.Payload!.Contains("ORIGINAL-123"), "Activation used wrong hardware identity");
            foreach (var code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.ServiceUnavailable })
            {
                handler.Code = code;
                await MustReject(() => gate.VerifyAsync(trial, "credential", machine));
            }
            handler.Code = HttpStatusCode.OK;
            handler.ThrowOffline = true;
            await MustReject(() => gate.VerifyAsync(trial, "cached-credential", machine));
            handler.ThrowOffline = false; handler.ThrowTimeout = true;
            await MustReject(() => gate.VerifyAsync(trial, "cached-credential", machine));
            handler.ThrowTimeout = false;
            handler.Body = "{\"token\":\"\",\"machineId\":0}";
            await MustReject(() => gate.VerifyAsync(trial, "credential", machine));
        }
        handler.Body = JsonSerializer.Serialize(new { token = "expired", machineId = 123, trialEndsAt = DateTime.UtcNow.AddDays(-1) });
        await MustReject(() => gate.VerifyAsync(true, "expired@example.test", machine));
        await MustReject(() => gate.VerifyAsync(false, "", machine));

        var storage = LinuxStorageDiagnostic.ParseDevices("""
            {"blockdevices":[
              {"name":"nvme0n1","type":"disk","size":256060514304,"rota":false,"rm":false,"model":"Real NVMe","serial":"NVME-123","tran":"nvme"},
              {"name":"sda","type":"disk","size":32000000000,"rota":false,"rm":false,"model":"Boot stick","tran":"usb"},
              {"name":"sdb","type":"disk","size":"1000000000","rota":"0","rm":"1","model":"Removable"},
              {"name":"sdc","type":"disk","size":1000000000,"rota":false,"rm":false,"children":[{"mountpoint":"/run/live/medium"}]}
            ]}
            """);
        Check(storage.Devices.Count == 1 && storage.Devices[0].SizeGB > 238 && storage.Devices[0].IsSsd,
            "Numeric lsblk fields or USB filtering failed");
        string nvme = """
            smartctl 7.4
            Model Number: Real NVMe
            Serial Number: NVME-123
            SMART overall-health self-assessment test result: PASSED
            Temperature: 42 Celsius
            Percentage Used: 19%
            Power On Hours: 1,234
            """;
        var commands = new List<string>();
        var smart = new LinuxSmartTestService((_, args, _) =>
        {
            commands.Add(args);
            return args == "--version" ? "smartctl 7.4" : args == "--scan"
                ? "/dev/nvme0 -d nvme # NVMe\n/dev/sda -d scsi # Boot USB" : nvme;
        }, storage.Devices.Select(d => d.DeviceId));
        var health = smart.QuickHealthCheck();
        var result = LinuxStorageChecks.ApplyHealth(storage, health, smart.Errors);
        Check(result.Passed && !storage.IsInconclusive && storage.Devices[0].HealthPercent == 81, "NVMe health not applied");
        Check(storage.Devices[0].PowerOnHours == 1234 && storage.Devices[0].Temperature == 42, "NVMe telemetry incorrect");
        Check(!commands.Any(c => c.Contains("-a /dev/sda")), "SMART queried boot USB");
        var unavailable = new LinuxSmartTestService((_, args, _) => args == "--version" ? "smartctl 7.4" : "Permission denied",
            new[] { "/dev/nvme0n1" });
        var failedHealth = unavailable.QuickHealthCheck();
        storage.Devices[0].HealthPercent = null;
        result = LinuxStorageChecks.ApplyHealth(storage, failedHealth, unavailable.Errors);
        Check(!result.Passed && storage.IsInconclusive && result.Details.Any(d => d.Contains("Permission denied")), "Unreadable SMART became a pass or lost error");
        Check(!new LinuxSmartTestService((_, args, _) => args == "--version" ? "smartctl 7.4" : "", Array.Empty<string>()).QuickHealthCheck().OverallHealthy,
            "Zero SMART devices falsely passed");
        Console.WriteLine("PASS: online activation fails closed, expired trials rejected, numeric storage discovery, USB exclusion and NVMe SMART health.");
    }

    static async Task MustReject(Func<Task<VerifiedActivation>> operation)
    {
        try { await operation(); }
        catch { return; }
        throw new Exception("Unverified/offline activation allowed testing");
    }

    sealed class ActivationHandler : HttpMessageHandler
    {
        public HttpStatusCode Code;
        public string Body = "";
        public string? Payload;
        public bool ThrowOffline, ThrowTimeout;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Payload = await request.Content!.ReadAsStringAsync();
            if (ThrowOffline) throw new HttpRequestException("offline");
            if (ThrowTimeout) throw new TaskCanceledException("timeout");
            return new HttpResponseMessage(Code) { Content = new StringContent(Body) };
        }
    }
}
