namespace LaptopQC.Hardware.Providers;

public interface ISensorProvider : IDisposable
{
    void Initialize();
    void Update();
    double? GetCpuTemperature();
    double? GetCpuClockSpeed();
    bool HasDiscreteGpu();
    string? GetDiscreteGpuName();
    double? GetGpuTemperature();
    double? GetGpuLoad();
    double? GetGpuClockSpeed();
    StorageSmartData? GetStorageHealth(string modelName);
    BatteryData? GetBatteryData();
}

public class StorageSmartData
{
    public int? HealthPercent { get; set; }
    public int? Temperature { get; set; }
    public int? PowerOnHours { get; set; }
    public long? TotalBytesWritten { get; set; }
}

public class BatteryData
{
    public uint DesignedCapacity { get; set; }
    public uint FullChargedCapacity { get; set; }
    public int DegradationLevel { get; set; }
    public int? CycleCount { get; set; }
}

