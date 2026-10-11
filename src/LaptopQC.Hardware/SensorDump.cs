#if WINDOWS
using LibreHardwareMonitor.Hardware;

class Program
{
    static void Main()
    {
        Console.WriteLine("Dumping all sensors...");
        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
            IsBatteryEnabled = true,
            IsControllerEnabled = true
        };

        computer.Open();
        
        string logPath = Path.Combine(Path.GetTempPath(), "sensors_dump.txt");
        using var writer = new StreamWriter(logPath, append: false);

        foreach (var hardware in computer.Hardware)
        {
            Log(writer, $"Hardware: {hardware.Name} ({hardware.HardwareType})");
            hardware.Update();

            foreach (var subHardware in hardware.SubHardware)
            {
                 Log(writer, $"\tSubHardware: {subHardware.Name}");
                 subHardware.Update();
            }

            foreach (var sensor in hardware.Sensors)
            {
                Log(writer, $"\tSensor: {sensor.Name} ({sensor.SensorType}) = {sensor.Value}");
            }
        }
        
        computer.Close();
        Console.WriteLine($"Done. Saved to {logPath}");
    }

    static void Log(StreamWriter w, string msg)
    {
        Console.WriteLine(msg);
        w.WriteLine(msg);
    }
}
#endif
