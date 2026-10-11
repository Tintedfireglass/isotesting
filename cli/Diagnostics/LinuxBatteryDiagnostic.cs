using System.Text.RegularExpressions;
using LaptopQC.Core.Abstractions;
using LaptopQC.Core.Diagnostics;

namespace Pramaan.CLI.Diagnostics;

public class LinuxBatteryDiagnostic : IBatteryDiagnostic
{
    private const string SysBatPath = "/sys/class/power_supply";

    public BatteryInfo GetInfo()
    {
        var info = new BatteryInfo();

        try
        {
            if (!Directory.Exists(SysBatPath))
            {
                info.IsPresent = false;
                return info;
            }

            // Find battery directories (BAT0, BAT1, etc.)
            var batDirs = Directory.GetDirectories(SysBatPath)
                .Where(d =>
                {
                    var type = LinuxCommandRunner.ReadFile(Path.Combine(d, "type"));
                    return type.Equals("Battery", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            if (batDirs.Count == 0)
            {
                info.IsPresent = false;
                return info;
            }

            info.IsPresent = true;

            // Use the first battery
            var bat = batDirs[0];

            // Capacity percentage
            var capacityStr = LinuxCommandRunner.ReadFile(Path.Combine(bat, "capacity"));
            if (int.TryParse(capacityStr, out int cap))
                info.EstimatedChargeRemaining = cap;

            // Status (Charging, Discharging, Full, Unknown)
            var status = LinuxCommandRunner.ReadFile(Path.Combine(bat, "status"));
            info.Status = status;
            info.BatteryStatus = status;

            // Energy/charge values (prefer energy_* over charge_*)
            var energyFullDesign = ReadSysLong(bat, "energy_full_design");
            var energyFull = ReadSysLong(bat, "energy_full");
            var energyNow = ReadSysLong(bat, "energy_now");

            if (energyFullDesign == 0)
            {
                // Fallback to charge_* (mAh × voltage)
                var chargeFullDesign = ReadSysLong(bat, "charge_full_design");
                var chargeFull = ReadSysLong(bat, "charge_full");
                var voltage = ReadSysLong(bat, "voltage_now"); // µV
                if (voltage > 0)
                {
                    energyFullDesign = chargeFullDesign * voltage / 1_000_000_000L; // µAh × µV → µWh → mWh
                    energyFull = chargeFull * voltage / 1_000_000_000L;
                }
            }

            // Convert µWh → mWh
            info.DesignedCapacityMWh = (uint)(energyFullDesign / 1000);
            info.FullChargedCapacityMWh = (uint)(energyFull / 1000);

            // Health %
            if (energyFullDesign > 0 && energyFull > 0)
            {
                double healthFraction = (double)energyFull / energyFullDesign;
                info.HealthPercent = (int)(healthFraction * 100);
                info.WearLevelPercent = Math.Max(0, 100 - info.HealthPercent.Value);
            }

            // Cycle count (not always available in sysfs)
            var cycleStr = LinuxCommandRunner.ReadFile(Path.Combine(bat, "cycle_count"));
            if (int.TryParse(cycleStr, out int cycles) && cycles > 0)
                info.CycleCount = cycles;

            // Manufacturer / model
            info.ManufactureName = LinuxCommandRunner.ReadFile(Path.Combine(bat, "manufacturer"));
            info.Name = LinuxCommandRunner.ReadFile(Path.Combine(bat, "model_name"));

            // Tamper check
            if (info.DesignedCapacityMWh > 0 && info.FullChargedCapacityMWh > info.DesignedCapacityMWh * 1.15)
            {
                info.IsTampered = true;
                info.TamperReason = "Battery Tampered - Full charge exceeds design capacity";
                info.HealthPercent = null;
                info.WearLevelPercent = null;
            }
        }
        catch
        {
            info.IsPresent = false;
        }

        return info;
    }

    public (bool IsHealthy, string Message) ValidateBattery(BatteryInfo info)
    {
        if (!info.IsPresent) return (true, "No battery (desktop system)");
        if (info.IsTampered) return (false, info.TamperReason ?? "Battery Tampered");
        if (info.HealthPercent.HasValue && info.HealthPercent.Value < 50)
            return (false, $"Battery health critical: {info.HealthPercent}%");
        if (info.WearLevelPercent.HasValue && info.WearLevelPercent.Value > 40)
            return (false, $"Battery wear critical: {info.WearLevelPercent}%");
        var cycleLabel = info.CycleCount.HasValue ? $", {info.CycleCount} cycles" : "";
        var healthLabel = info.HealthPercent.HasValue ? $"{info.HealthPercent}%" : "N/A";
        return (true, $"Battery OK: {healthLabel} health{cycleLabel}");
    }

    private static long ReadSysLong(string dir, string file)
    {
        var raw = LinuxCommandRunner.ReadFile(Path.Combine(dir, file));
        return long.TryParse(raw, out long v) ? v : 0;
    }
}
