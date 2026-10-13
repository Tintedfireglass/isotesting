using LaptopQC.Core.Diagnostics;
using LaptopQC.Core.Models;

namespace Pramaan.CLI.Diagnostics;

public static class LinuxStorageChecks
{
    public static TestResult ApplyHealth(StorageInfo storage, SmartHealthCheckResult health, IEnumerable<string>? errors = null)
    {
        var result = new TestResult { Tested = true };
        result.Details.Add(health.Message);
        foreach (var drive in storage.Devices)
        {
            var smart = health.Devices.FirstOrDefault(d => LinuxSmartTestService.MatchesDevice(drive.DeviceId, d.DevicePath));
            if (smart == null) { drive.HealthPercent = null; result.Details.Add($"{drive.DeviceId}: SMART health unavailable."); continue; }
            drive.HealthPercent = smart.HealthScore;
            drive.Temperature = smart.Temperature;
            drive.PowerOnHours = smart.PowerOnHours;
            if (!string.IsNullOrWhiteSpace(smart.Model) && !smart.Model.StartsWith("/dev/")) drive.Model = smart.Model;
            if (!string.IsNullOrWhiteSpace(smart.SerialNumber)) drive.SerialNumber = smart.SerialNumber;
            result.Details.Add($"{drive.DeviceId} ({drive.Model}): SMART {(smart.HealthPassed ? "PASS" : "FAIL")}, health {smart.HealthScore}%.");
        }
        if (errors != null) result.Details.AddRange(errors);
        bool missing = storage.Devices.Count == 0 || storage.Devices.Any(d => !d.HealthPercent.HasValue);
        storage.IsInconclusive = missing;
        storage.InconclusiveReason = missing ? "Internal drive SMART health unavailable. See Details for the command error." : "";
        result.Passed = !missing && health.Devices.All(d => d.HealthPassed) && !storage.IsTampered
            && storage.RaidArrays.All(r => r.State != "Degraded");
        result.Message = missing ? storage.InconclusiveReason! : result.Passed
            ? $"{storage.Devices.Count} internal drive(s) healthy (SMART)" : "Internal storage SMART health warning. See Details.";
        return result;
    }
}
