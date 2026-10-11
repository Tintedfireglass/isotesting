#if WINDOWS
using LaptopQC.Core.Abstractions;
using LaptopQC.Hardware.Providers;

namespace LaptopQC.Core.Diagnostics;

/// <summary>
/// Provides battery detection and health diagnostics
/// </summary>
public class BatteryDiagnostic : IBatteryDiagnostic
{
    private readonly IWmiProvider _wmi;
    private readonly ISensorProvider? _sensors;

    public BatteryDiagnostic(IWmiProvider? wmiProvider = null, ISensorProvider? sensors = null)
    {
        _wmi = wmiProvider ?? new WmiProvider();
        _sensors = sensors;
    }

    /// <summary>
    /// Gets battery information and health
    /// </summary>
    public BatteryInfo GetInfo()
    {
        var info = new BatteryInfo();

        // Check if this is a laptop with battery
        foreach (var obj in _wmi.Query("Win32_Battery"))
        {
            info.IsPresent = true;
            info.Name = _wmi.GetValue<string>(obj, "Name", "Battery") ?? "Battery";
            info.Status = _wmi.GetValue<string>(obj, "Status", "Unknown") ?? "Unknown";
            info.EstimatedChargeRemaining = _wmi.GetValue<int>(obj, "EstimatedChargeRemaining", 0);
            info.BatteryStatus = MapBatteryStatus(_wmi.GetValue<int>(obj, "BatteryStatus", 0));
            break;
        }

        if (!info.IsPresent)
            return info;

        // Get detailed capacity info from BatteryFullChargedCapacity
        int? wmiCycleCount = null;
        bool wmiCycleSeen = false;
        try
        {
            foreach (var obj in _wmi.Query("BatteryFullChargedCapacity", "root\\WMI"))
            {
                info.FullChargedCapacityMWh = _wmi.GetValue<uint>(obj, "FullChargedCapacity", 0);
                break;
            }

            foreach (var obj in _wmi.Query("BatteryStaticData", "root\\WMI"))
            {
                info.DesignedCapacityMWh = _wmi.GetValue<uint>(obj, "DesignedCapacity", 0);
                info.ManufactureName = _wmi.GetValue<string>(obj, "ManufactureName", "") ?? "";
                info.SerialNumber = _wmi.GetValue<string>(obj, "SerialNumber", "") ?? "";
                info.Chemistry = _wmi.GetValue<string>(obj, "Chemistry", "") ?? "";
                info.PartNumber = _wmi.GetValue<string>(obj, "DeviceName", "") ?? "";
                break;
            }

            // Get cycle count if available
            foreach (var obj in _wmi.Query("BatteryCycleCount", "root\\WMI"))
            {
                wmiCycleSeen = true;
                var raw = _wmi.GetValue<uint>(obj, "CycleCount", 0);
                wmiCycleCount = (int)raw;
                break;
            }
        }
        catch
        {
            // WMI battery queries can fail on some systems
        }

        // Try LibreHardwareMonitor for additional data
        int? sensorDegradation = null;
        int? sensorCycleCount = null;
        try
        {
            BatteryData? batteryData = null;
            if (_sensors != null)
            {
                batteryData = _sensors.GetBatteryData();
            }
            else
            {
                using var sensors = new SensorProvider();
                sensors.Initialize();
                batteryData = sensors.GetBatteryData();
            }
            if (batteryData != null)
            {
                if (info.DesignedCapacityMWh == 0)
                    info.DesignedCapacityMWh = batteryData.DesignedCapacity;
                if (info.FullChargedCapacityMWh == 0)
                    info.FullChargedCapacityMWh = batteryData.FullChargedCapacity;
                if (batteryData.DegradationLevel > 0)
                    sensorDegradation = batteryData.DegradationLevel;
                if (batteryData.CycleCount.HasValue && batteryData.CycleCount.Value > 0)
                    sensorCycleCount = batteryData.CycleCount.Value;
            }
        }
        catch { }

        // Calculate wear level
        if (info.DesignedCapacityMWh > 0 && info.FullChargedCapacityMWh > 0)
        {
            var wear = (int)Math.Round(
                (1 - ((double)info.FullChargedCapacityMWh / info.DesignedCapacityMWh)) * 100);
            wear = Math.Clamp(wear, 0, 100);
            info.WearLevelPercent = wear;
            info.HealthPercent = 100 - wear;
        }

        // If sensor degradation exists and conflicts with WMI, prefer the worse (more worn) value.
        if (sensorDegradation.HasValue)
        {
            var degradation = Math.Clamp(sensorDegradation.Value, 0, 100);
            if (!info.WearLevelPercent.HasValue || info.WearLevelPercent.Value == 0 || degradation > info.WearLevelPercent.Value)
            {
                info.WearLevelPercent = degradation;
                info.HealthPercent = 100 - degradation;
            }
        }

        // Resolve cycle count: prefer sensor data, else use WMI if it looks reliable.
        if (sensorCycleCount.HasValue)
        {
            info.CycleCount = sensorCycleCount.Value;
        }
        else if (wmiCycleSeen)
        {
            if (wmiCycleCount.HasValue && wmiCycleCount.Value > 0)
            {
                info.CycleCount = wmiCycleCount.Value;
            }
            else
            {
                // WMI often reports 0 when cycle count is unavailable.
                // Treat 0 as unknown to avoid misleading "0 cycles".
                info.CycleCount = null;
            }
        }

        // Validate BMS-reported data for obvious impossibilities/unreadable values.
        // If tampered/unreadable, clear derived metrics to prevent misleading scoring.
        var (isTampered, reason) = EvaluateTamperState(info, sensorDegradation);
        if (isTampered)
        {
            info.IsTampered = true;
            info.TamperReason = reason;
            info.WearLevelPercent = null;
            info.HealthPercent = null;
        }

        return info;
    }

    private static (bool IsTampered, string Reason) EvaluateTamperState(BatteryInfo info, int? sensorDegradation)
    {
        if (!info.IsPresent) return (false, "");

        // Missing capacity numbers => we can't trust BMS data enough to certify health.
        if (info.DesignedCapacityMWh == 0 || info.FullChargedCapacityMWh == 0)
            return (true, "Battery Tampered - Unable to read data");

        // Full charged capacity should not exceed design capacity by a material amount.
        // Allow a small tolerance for firmware rounding differences.
        if (info.FullChargedCapacityMWh > info.DesignedCapacityMWh * 1.10)
            return (true, "Battery Tampered - Unable to read data");

        // If we have two independent wear signals and they strongly disagree, treat as unreliable.
        if (sensorDegradation.HasValue && info.WearLevelPercent.HasValue)
        {
            var delta = Math.Abs(sensorDegradation.Value - info.WearLevelPercent.Value);
            if (delta >= 35)
                return (true, "Battery Tampered - Unable to read data");
        }

        return (false, "");
    }

    private string MapBatteryStatus(int status)
    {
        return status switch
        {
            1 => "Discharging",
            2 => "Charging",
            3 => "Fully Charged",
            4 => "Low",
            5 => "Critical",
            6 => "Charging (High)",
            7 => "Charging (Low)",
            8 => "Charging (Critical)",
            9 => "Undefined",
            10 => "Partially Charged",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Validates battery health
    /// </summary>
    public (bool IsHealthy, string Message) ValidateBattery(BatteryInfo info)
    {
        if (!info.IsPresent)
            return (true, "No battery (desktop system)");

        if (info.IsTampered)
            return (false, string.IsNullOrWhiteSpace(info.TamperReason) ? "Battery Tampered - Unable to read data" : info.TamperReason);

        if (info.WearLevelPercent > 40)
            return (false, $"Battery wear level critical: {info.WearLevelPercent}% worn");

        if (info.WearLevelPercent > 20)
            return (true, $"Battery wear level high: {info.WearLevelPercent}% worn");

        if (info.HealthPercent.HasValue)
            return (true, $"Battery health: {info.HealthPercent}%");

        return (true, "Battery present");
    }
}
#endif

