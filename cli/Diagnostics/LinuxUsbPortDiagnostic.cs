using System.Text.RegularExpressions;
using LaptopQC.Core.Models;

namespace Pramaan.CLI.Diagnostics;

/// <summary>
/// Interactive USB port diagnostic that guides the technician through testing each USB port
/// </summary>
public class LinuxUsbPortDiagnostic
{
    public class UsbPortTestResult
    {
        public int TotalPorts { get; set; }
        public int Usb3Ports { get; set; }
        public int Usb2Ports { get; set; }
        public int UsbCPorts { get; set; }
        public List<PortTest> TestedPorts { get; set; } = new();
        public bool AllPortsWorking => TestedPorts.All(p => p.Passed);
        public int WorkingPortsCount => TestedPorts.Count(p => p.Passed);
        public string Summary => $"{WorkingPortsCount}/{TestedPorts.Count} ports working";
    }

    public class PortTest
    {
        public string PortName { get; set; } = "";
        public string PortType { get; set; } = "";  // USB 2.0, USB 3.x, USB-C
        public bool Passed { get; set; }
        public string DeviceDetected { get; set; } = "";
        public DateTime TestedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Scans the system to identify all available USB ports
    /// </summary>
    public UsbPortInfo DetectUsbPorts()
    {
        var info = new UsbPortInfo();
        
        try
        {
            // Method 1: Parse lsusb output for USB controllers
            var lsusb = LinuxCommandRunner.TryRun("lsusb", "-t");
            var usb3Count = Regex.Matches(lsusb, @"5000M|10000M|20000M", RegexOptions.IgnoreCase).Count;
            var usb2Count = Regex.Matches(lsusb, @"480M", RegexOptions.IgnoreCase).Count;
            
            info.Usb3Ports = usb3Count;
            info.Usb2Ports = usb2Count;
            
            // Method 2: Check /sys/bus/usb/devices/ for physical ports
            var usbSysPath = "/sys/bus/usb/devices";
            if (Directory.Exists(usbSysPath))
            {
                var ports = Directory.GetDirectories(usbSysPath)
                    .Where(d => Regex.IsMatch(Path.GetFileName(d), @"^\d+-\d+$"))
                    .ToList();
                
                if (ports.Count > info.Usb2Ports + info.Usb3Ports)
                {
                    info.TotalPorts = ports.Count;
                }
            }
            
            // Method 3: Check for USB-C ports via lsusb and typec class
            if (Directory.Exists("/sys/class/typec"))
            {
                info.UsbCPorts = Directory.GetDirectories("/sys/class/typec").Length;
            }
            
            // Ensure total is at least the sum of USB 2 + USB 3
            if (info.TotalPorts < info.Usb2Ports + info.Usb3Ports)
            {
                info.TotalPorts = info.Usb2Ports + info.Usb3Ports;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"USB port detection error: {ex.Message}");
        }

        return info;
    }

    public class UsbPortInfo
    {
        public int TotalPorts { get; set; }
        public int Usb2Ports { get; set; }
        public int Usb3Ports { get; set; }
        public int UsbCPorts { get; set; }
    }

    /// <summary>
    /// Monitors USB device connections in real-time by watching kernel messages
    /// </summary>
    public async Task<string?> WaitForUsbDeviceAsync(int timeoutSeconds = 15)
    {
        var startTime = DateTime.Now;
        var initialDevices = GetConnectedUsbDeviceIds();
        
        while ((DateTime.Now - startTime).TotalSeconds < timeoutSeconds)
        {
            await Task.Delay(500);
            
            var currentDevices = GetConnectedUsbDeviceIds();
            var newDevices = currentDevices.Except(initialDevices).ToList();
            
            if (newDevices.Any())
            {
                // Get device details
                return GetDeviceNameById(newDevices.First());
            }
        }
        
        return null; // Timeout
    }

    /// <summary>
    /// Gets all currently connected USB device IDs
    /// </summary>
    private HashSet<string> GetConnectedUsbDeviceIds()
    {
        var devices = new HashSet<string>();
        
        try
        {
            var lsusb = LinuxCommandRunner.TryRun("lsusb", "");
            foreach (var line in lsusb.Split('\n'))
            {
                // Bus 001 Device 005: ID 0bda:8153 Realtek Semiconductor Corp.
                var match = Regex.Match(line, @"Bus (\d+) Device (\d+): ID ([\w:]+)");
                if (match.Success)
                {
                    devices.Add($"{match.Groups[1].Value}:{match.Groups[2].Value}");
                }
            }
        }
        catch { }
        
        return devices;
    }

    /// <summary>
    /// Gets the human-readable name of a USB device by its ID
    /// </summary>
    private string GetDeviceNameById(string deviceId)
    {
        try
        {
            var lsusb = LinuxCommandRunner.TryRun("lsusb", "");
            var parts = deviceId.Split(':');
            if (parts.Length == 2)
            {
                var busNum = parts[0].PadLeft(3, '0');
                var devNum = parts[1].PadLeft(3, '0');
                
                foreach (var line in lsusb.Split('\n'))
                {
                    if (line.Contains($"Bus {busNum} Device {devNum}"))
                    {
                        var match = Regex.Match(line, @"ID [\w:]+\s+(.+)$");
                        if (match.Success)
                        {
                            return match.Groups[1].Value.Trim();
                        }
                    }
                }
            }
        }
        catch { }
        
        return "Unknown USB Device";
    }

    /// <summary>
    /// Interactive test that prompts technician to test each port
    /// </summary>
    public async Task<UsbPortTestResult> RunInteractiveTestAsync(
        Action<string>? statusCallback = null)
    {
        var result = new UsbPortTestResult();
        var portInfo = DetectUsbPorts();
        
        result.TotalPorts = portInfo.TotalPorts;
        result.Usb2Ports = portInfo.Usb2Ports;
        result.Usb3Ports = portInfo.Usb3Ports;
        result.UsbCPorts = portInfo.UsbCPorts;
        
        statusCallback?.Invoke($"Detected {result.TotalPorts} USB ports (USB 3.x: {result.Usb3Ports}, USB 2.0: {result.Usb2Ports}, USB-C: {result.UsbCPorts})");
        
        // If no ports detected or detection failed, ask user
        if (result.TotalPorts == 0)
        {
            statusCallback?.Invoke("Could not auto-detect ports. Manual entry required.");
            return result;
        }

        // Test USB 3.x ports
        for (int i = 1; i <= result.Usb3Ports; i++)
        {
            statusCallback?.Invoke($"Testing USB 3.x Port {i}/{result.Usb3Ports}...");
            var portTest = await TestSinglePortAsync($"USB 3.x Port #{i}", "USB 3.x", statusCallback);
            result.TestedPorts.Add(portTest);
        }

        // Test USB 2.0 ports
        for (int i = 1; i <= result.Usb2Ports; i++)
        {
            statusCallback?.Invoke($"Testing USB 2.0 Port {i}/{result.Usb2Ports}...");
            var portTest = await TestSinglePortAsync($"USB 2.0 Port #{i}", "USB 2.0", statusCallback);
            result.TestedPorts.Add(portTest);
        }

        // Test USB-C ports separately if detected
        for (int i = 1; i <= result.UsbCPorts; i++)
        {
            statusCallback?.Invoke($"Testing USB-C Port {i}/{result.UsbCPorts}...");
            var portTest = await TestSinglePortAsync($"USB-C Port #{i}", "USB-C", statusCallback);
            result.TestedPorts.Add(portTest);
        }

        return result;
    }

    /// <summary>
    /// Tests a single USB port by waiting for device insertion
    /// </summary>
    private async Task<PortTest> TestSinglePortAsync(
        string portName, 
        string portType,
        Action<string>? statusCallback = null)
    {
        statusCallback?.Invoke($"Insert a USB device into {portName}... (15s timeout)");
        
        var deviceName = await WaitForUsbDeviceAsync(15);
        
        return new PortTest
        {
            PortName = portName,
            PortType = portType,
            Passed = deviceName != null,
            DeviceDetected = deviceName ?? "No device detected",
            TestedAt = DateTime.Now
        };
    }

    /// <summary>
    /// Quick validation: checks if USB subsystem is functional
    /// </summary>
    public (bool IsHealthy, string Message) QuickValidation()
    {
        try
        {
            var lsusb = LinuxCommandRunner.TryRun("lsusb", "");
            if (string.IsNullOrWhiteSpace(lsusb))
            {
                return (false, "USB subsystem not responding");
            }

            var deviceCount = lsusb.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            if (deviceCount == 0)
            {
                return (false, "No USB devices detected");
            }

            var portInfo = DetectUsbPorts();
            if (portInfo.TotalPorts == 0)
            {
                return (false, "No USB ports detected");
            }

            return (true, $"USB OK: {portInfo.TotalPorts} ports detected ({deviceCount} devices connected)");
        }
        catch (Exception ex)
        {
            return (false, $"USB check failed: {ex.Message}");
        }
    }
}
