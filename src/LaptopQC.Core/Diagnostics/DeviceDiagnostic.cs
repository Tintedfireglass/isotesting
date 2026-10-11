#if WINDOWS
using LaptopQC.Core.Abstractions;
using LaptopQC.Hardware.Models;
using LaptopQC.Hardware.Providers;
using System.Text.RegularExpressions;

namespace LaptopQC.Core.Diagnostics;

/// <summary>
/// Provides detection of all connected devices and ports
/// </summary>
public class DeviceDiagnostic : IDeviceDiagnostic
{
    private readonly IWmiProvider _wmi;

    public DeviceDiagnostic(IWmiProvider? wmiProvider = null)
    {
        _wmi = wmiProvider ?? new WmiProvider();
    }

    /// <summary>
    /// Gets information about all devices and ports
    /// </summary>
    public DevicesInfo GetInfo()
    {
        var info = new DevicesInfo();

        // Detect all device types
        DetectInputDevices(info);
        DetectUsbPorts(info);
        DetectConnectedUsbDevices(info);
        DetectDisplays(info);
        DetectGpus(info);
        DetectAudioDevices(info);
        DetectNetworkDevices(info);
        DetectCamera(info);

        return info;
    }

    /// <summary>
    /// Detects keyboard, trackpad, and mouse devices
    /// </summary>
    private void DetectInputDevices(DevicesInfo info)
    {
        // Detect Keyboards
        foreach (var obj in _wmi.Query("Win32_Keyboard"))
        {
            var device = new InputDevice
            {
                Name = _wmi.GetValue<string>(obj, "Name", "Unknown Keyboard") ?? "Unknown Keyboard",
                DeviceId = _wmi.GetValue<string>(obj, "DeviceID", "") ?? "",
                Description = _wmi.GetValue<string>(obj, "Description", "") ?? "",
                Status = _wmi.GetValue<string>(obj, "Status", "Unknown") ?? "Unknown",
                Type = InputDeviceType.Keyboard
            };
            info.InputDevices.Add(device);
        }

        // Detect Pointing Devices (Mouse, Trackpad)
        foreach (var obj in _wmi.Query("Win32_PointingDevice"))
        {
            var name = _wmi.GetValue<string>(obj, "Name", "") ?? "";
            var pointingType = _wmi.GetValue<ushort>(obj, "PointingType", 0);
            var deviceInterface = _wmi.GetValue<ushort>(obj, "DeviceInterface", 0);
            
            var device = new InputDevice
            {
                Name = name,
                DeviceId = _wmi.GetValue<string>(obj, "DeviceID", "") ?? "",
                Description = _wmi.GetValue<string>(obj, "Description", "") ?? "",
                Status = _wmi.GetValue<string>(obj, "Status", "Unknown") ?? "Unknown",
                Type = DeterminePointingDeviceType(name, pointingType)
            };
            
            info.InputDevices.Add(device);
        }
    }

    /// <summary>
    /// Determines if a pointing device is a trackpad, mouse, or other
    /// </summary>
    private InputDeviceType DeterminePointingDeviceType(string name, ushort pointingType)
    {
        var nameLower = name.ToLower();
        
        // Check for trackpad/touchpad indicators
        // PointingType 5 = Touch Pad per WMI spec
        if (pointingType == 5 || 
            nameLower.Contains("touchpad") ||
            nameLower.Contains("trackpad") ||
            nameLower.Contains("clickpad") ||
            nameLower.Contains("precision") ||
            nameLower.Contains("synaptics") ||
            nameLower.Contains("elan") ||
            nameLower.Contains("alps") ||
            nameLower.Contains("i2c hid") ||
            nameLower.Contains("smbus") ||
            nameLower.Contains("ps/2") ||  // PS/2 pointing devices are usually internal (trackpad)
            nameLower.Contains("hid-compliant mouse") || // Many trackpads report as this
            (nameLower.Contains("hid") && nameLower.Contains("device") && !nameLower.Contains("usb"))) // Internal HID devices
        {
            return InputDeviceType.Trackpad;
        }
        
        // Check for touchscreen
        if (nameLower.Contains("touch screen") || nameLower.Contains("touchscreen"))
        {
            return InputDeviceType.Touchscreen;
        }
        
        // Default to mouse
        return InputDeviceType.Mouse;
    }

    /// <summary>
    /// Detects USB controllers to estimate available ports
    /// </summary>
    private void DetectUsbPorts(DevicesInfo info)
    {
        // Track USB hubs by version for more accurate counting
        var usb3HubCount = 0;
        var usb2HubCount = 0;
        
        // First, analyze USB Hubs to get accurate port counts and versions
        foreach (var obj in _wmi.Query("Win32_USBHub"))
        {
            var name = _wmi.GetValue<string>(obj, "Name", "") ?? "";
            var deviceId = _wmi.GetValue<string>(obj, "DeviceID", "") ?? "";
            
            // Skip non-root hubs (external hubs)
            if (!name.Contains("Root Hub", StringComparison.OrdinalIgnoreCase))
                continue;
            
            // Determine USB version from hub name
            var hubVersion = DetermineUsbVersionFromHub(name);
            
            if (hubVersion.StartsWith("3."))
            {
                usb3HubCount++;
            }
            else if (hubVersion.StartsWith("2."))
            {
                usb2HubCount++;
            }
        }
        
        // Query USB Controllers  
        foreach (var obj in _wmi.Query("Win32_USBController"))
        {
            var name = _wmi.GetValue<string>(obj, "Name", "") ?? "";
            var deviceId = _wmi.GetValue<string>(obj, "DeviceID", "") ?? "";
            
            var controllerVersion = DetermineUsbVersion(name, deviceId);
            
            var port = new UsbPortInfo
            {
                ControllerName = name,
                DeviceId = deviceId,
                Status = _wmi.GetValue<string>(obj, "Status", "Unknown") ?? "Unknown",
                UsbVersion = controllerVersion,
                PortCount = EstimatePortCount(name)
            };
            
            info.UsbPorts.Add(port);
        }
        
        // If we detected USB 2.0 root hubs but no USB 2.0 controllers are showing,
        // this means there are USB 2.0-only ports that need to be accounted for
        var hasUsb2Entry = info.UsbPorts.Any(p => p.UsbVersion.StartsWith("2."));
        var hasUsb3Entry = info.UsbPorts.Any(p => p.UsbVersion.StartsWith("3."));
        
        // Modern systems with xHCI controllers may have companion USB 2.0 hubs
        // for USB 2.0-only physical ports. Detect these via registry or hub analysis.
        if (usb2HubCount > 0 && !hasUsb2Entry)
        {
            // Add USB 2.0 port entry based on detected hubs
            info.UsbPorts.Add(new UsbPortInfo
            {
                ControllerName = "USB 2.0 Root Hub",
                DeviceId = "USB2_HUB",
                Status = "OK",
                UsbVersion = "2.0",
                PortCount = usb2HubCount  // At least 1 port per hub
            });
        }
        
        // Adjust xHCI controller port counts based on actual hub detection
        if (usb3HubCount > 0)
        {
            var xhciController = info.UsbPorts.FirstOrDefault(p => 
                p.ControllerName.Contains("xHCI", StringComparison.OrdinalIgnoreCase));
            if (xhciController != null)
            {
                // USB 3.0 ports are typically the SuperSpeed hubs
                xhciController.PortCount = Math.Max(xhciController.PortCount, usb3HubCount);
            }
        }
    }
    
    /// <summary>
    /// Determines USB version from hub name (more accurate than controller name)
    /// </summary>
    private string DetermineUsbVersionFromHub(string hubName)
    {
        var name = hubName.ToLower();
        
        // SuperSpeed indicates USB 3.x
        if (name.Contains("superspeed") || name.Contains("super speed"))
        {
            if (name.Contains("usb 3.2") || name.Contains("usb3.2") || name.Contains("20 gbps") || name.Contains("gen 2x2"))
                return "3.2";
            if (name.Contains("usb 3.1") || name.Contains("usb3.1") || name.Contains("10 gbps") || name.Contains("gen 2"))
                return "3.1";
            return "3.0";
        }
        
        if (name.Contains("3.2"))
            return "3.2";
        if (name.Contains("3.1"))
            return "3.1";
        if (name.Contains("3.0") || name.Contains("usb 3") || name.Contains("usb3"))
            return "3.0";
        if (name.Contains("2.0") || name.Contains("usb 2") || name.Contains("usb2") || 
            name.Contains("enhanced") || name.Contains("ehci"))
            return "2.0";
        if (name.Contains("1.1") || name.Contains("uhci") || name.Contains("ohci"))
            return "1.1";
            
        return "2.0"; // Default for generic Root Hubs
    }

    /// <summary>
    /// Determines USB version from controller name
    /// </summary>
    private string DetermineUsbVersion(string controllerName, string deviceId)
    {
        var name = controllerName.ToLower();
        
        if (name.Contains("3.2") || name.Contains("usb 3.2"))
            return "3.2";
        if (name.Contains("3.1") || name.Contains("usb 3.1"))
            return "3.1";
        if (name.Contains("3.0") || name.Contains("xhci") || name.Contains("usb 3"))
            return "3.0";
        if (name.Contains("2.0") || name.Contains("ehci") || name.Contains("enhanced"))
            return "2.0";
        if (name.Contains("1.1") || name.Contains("uhci") || name.Contains("ohci"))
            return "1.1";
            
        return "2.0"; // Default assumption
    }

    /// <summary>
    /// Estimates port count based on controller type
    /// </summary>
    private int EstimatePortCount(string controllerName)
    {
        // xHCI controllers (USB 3.0+) typically handle multiple ports
        if (controllerName.ToLower().Contains("xhci"))
            return 2;
        return 1;
    }

    /// <summary>
    /// Detects currently connected USB devices
    /// </summary>
    private void DetectConnectedUsbDevices(DevicesInfo info)
    {
        // Query PnP entities with USB in DeviceID
        var query = "SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB%'";
        
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(query);
            foreach (System.Management.ManagementObject obj in searcher.Get())
            {
                var name = _wmi.GetValue<string>(obj, "Name", "") ?? "";
                var deviceId = _wmi.GetValue<string>(obj, "DeviceID", "") ?? "";
                
                // Skip root hubs and controllers (they're already counted in UsbPorts)
                if (name.Contains("Hub", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Controller", StringComparison.OrdinalIgnoreCase))
                    continue;
                
                // Skip if empty name
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var usbDevice = new UsbDevice
                {
                    Name = name,
                    DeviceId = deviceId,
                    Manufacturer = _wmi.GetValue<string>(obj, "Manufacturer", "") ?? "",
                    Status = _wmi.GetValue<string>(obj, "Status", "Unknown") ?? "Unknown"
                };
                
                info.ConnectedUsbDevices.Add(usbDevice);
            }
        }
        catch { /* Ignore query failures */ }
    }

    /// <summary>
    /// Detects connected displays/monitors
    /// </summary>
    private void DetectDisplays(DevicesInfo info)
    {
        var monitorIds = GetMonitorIdMap();
        var connectionTypes = GetConnectionTypeMap();

        foreach (var obj in _wmi.Query("Win32_DesktopMonitor"))
        {
            var pnpDeviceId = _wmi.GetValue<string>(obj, "PNPDeviceID", "") ?? "";
            var pnpKey = NormalizePnpId(pnpDeviceId);

            var display = new DisplayDevice
            {
                Name = _wmi.GetValue<string>(obj, "Name", "Unknown Display") ?? "Unknown Display",
                DeviceId = _wmi.GetValue<string>(obj, "DeviceID", "") ?? "",
                ScreenWidth = (int)_wmi.GetValue<uint>(obj, "ScreenWidth", 0),
                ScreenHeight = (int)_wmi.GetValue<uint>(obj, "ScreenHeight", 0),
                IsActive = _wmi.GetValue<ushort>(obj, "Availability", 0) == 3 // 3 = Running/Full Power
            };

            // Try to get precise connection type from WmiMonitorConnectionParams first,
            // then fall back to heuristic string matching as a last resort
            if (!string.IsNullOrWhiteSpace(pnpKey) && connectionTypes.TryGetValue(pnpKey, out var connType))
                display.ConnectionType = connType;
            else
                display.ConnectionType = DetermineDisplayConnection(display.DeviceId, display.Name);

            if (!string.IsNullOrWhiteSpace(pnpKey) && monitorIds.TryGetValue(pnpKey, out var id))
            {
                display.ManufacturerCode = id.ManufacturerCode;
                display.ProductCode = id.ProductCode;
                display.SerialNumber = id.SerialNumber;
                display.PartNumber = id.PartNumber;
            }
            
            info.Displays.Add(display);
        }

    }

    /// <summary>
    /// Detects GPUs / Video Controllers
    /// </summary>
    private void DetectGpus(DevicesInfo info)
    {
        foreach (var obj in _wmi.Query("Win32_VideoController"))
        {
            var gpu = new GpuInfo
            {
                Name = _wmi.GetValue<string>(obj, "Name", "Unknown GPU") ?? "Unknown GPU",
                DeviceId = _wmi.GetValue<string>(obj, "DeviceID", "") ?? "",
                Status = _wmi.GetValue<string>(obj, "Status", "Unknown") ?? "Unknown",
                DriverVersion = _wmi.GetValue<string>(obj, "DriverVersion", "") ?? "",
                CurrentResX = (int)_wmi.GetValue<uint>(obj, "CurrentHorizontalResolution", 0),
                CurrentResY = (int)_wmi.GetValue<uint>(obj, "CurrentVerticalResolution", 0),
                CurrentRefreshRate = _wmi.GetValue<uint>(obj, "CurrentRefreshRate", 0)
            };
            
            // AdapterRAM is often reported in bytes, but is unreliable on modern systems (often 0 or capped at 4GB)
            var adapterRam = _wmi.GetValue<uint>(obj, "AdapterRAM", 0);
            if (adapterRam > 0)
            {
                gpu.MemoryGB = adapterRam / (1024.0 * 1024 * 1024);
            }
            
            info.Gpus.Add(gpu);
        }
    }

    /// <summary>
    /// Determines display connection type from WmiMonitorConnectionParams VideoOutputTechnology values.
    /// Used only as a last-resort fallback when WmiMonitorConnectionParams is unavailable.
    /// Values: https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/wmimonitorconnectionparams
    /// </summary>
    private string DetermineDisplayConnection(string deviceId, string name)
    {
        var combined = (deviceId + name).ToLower();
        
        if (combined.Contains("internal") || combined.Contains("lvds") || combined.Contains("edp"))
            return "Internal";
        if (combined.Contains("hdmi"))
            return "HDMI";
        if (combined.Contains("displayport") || combined.Contains("dp"))
            return "DisplayPort";
        if (combined.Contains("vga") || combined.Contains("dsub"))
            return "VGA";
        if (combined.Contains("dvi"))
            return "DVI";
        if (combined.Contains("thunderbolt") || combined.Contains("usb-c"))
            return "USB-C/Thunderbolt";
            
        return "External";
    }

    /// <summary>
    /// Builds a map of PNP key → connection type string by reading WmiMonitorConnectionParams
    /// from root\WMI. This uses the VideoOutputTechnology property which is an exact enum
    /// value from the Windows Display Driver Model (WDDM) — far more reliable than
    /// string-sniffing device names.
    ///
    /// VideoOutputTechnology values (D3DKMDT_VIDEO_OUTPUT_TECHNOLOGY):
    ///   -1 / 0xFFFFFFFF = Uninitialized
    ///   0  = VGA
    ///   1  = S-Video
    ///   2  = Composite Video
    ///   3  = Component Video
    ///   4  = DVI
    ///   5  = HDMI
    ///   6  = LVDS (Internal)
    ///   8  = D-Jpn
    ///   9  = SDI
    ///   10 = DisplayPort External
    ///   11 = DisplayPort Embedded (Internal eDP)
    ///   12 = UDI External
    ///   13 = UDI Embedded
    ///   14 = SDTV Dongle
    ///   15 = Miracast
    ///   16 = Indirect Wired
    ///   2147483648 = Internal (legacy catch-all)
    /// </summary>
    private Dictionary<string, string> GetConnectionTypeMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var obj in _wmi.Query("WmiMonitorConnectionParams", "root\\WMI"))
            {
                var instanceName = obj["InstanceName"]?.ToString() ?? "";
                var key = NormalizePnpId(instanceName);
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                // VideoOutputTechnology can be uint or int depending on the driver
                var techRaw = obj["VideoOutputTechnology"];
                var tech = techRaw is uint u ? (int)u
                         : techRaw is int i ? i
                         : -1;

                map[key] = tech switch
                {
                    0  => "VGA",
                    4  => "DVI",
                    5  => "HDMI",
                    6  => "Internal (LVDS)",
                    10 => "DisplayPort",
                    11 => "Internal (eDP)",
                    15 => "Miracast (Wireless)",
                    16 => "Indirect Wired",
                    2147483647 => "Internal",  // 0x7FFFFFFF — some drivers report this
                    _ when tech < 0 => "Unknown",
                    _  => "External"
                };
            }
        }
        catch { /* root\WMI may be unavailable in some environments; caller falls back to string heuristics */ }

        return map;
    }

    /// <summary>
    /// Detects audio devices (speakers, microphones)
    /// </summary>
    private void DetectAudioDevices(DevicesInfo info)
    {
        foreach (var obj in _wmi.Query("Win32_SoundDevice"))
        {
            var name = _wmi.GetValue<string>(obj, "Name", "Unknown Audio Device") ?? "Unknown Audio Device";
            
            var audio = new AudioDevice
            {
                Name = name,
                DeviceId = _wmi.GetValue<string>(obj, "DeviceID", "") ?? "",
                Status = _wmi.GetValue<string>(obj, "Status", "Unknown") ?? "Unknown",
                IsOutput = true,  // Most sound devices are output
                IsInput = name.ToLower().Contains("microphone") || name.ToLower().Contains("input")
            };
            
            info.AudioDevices.Add(audio);
        }
    }

    /// <summary>
    /// Detects network adapters (Ethernet, WiFi, Bluetooth)
    /// </summary>
    private void DetectNetworkDevices(DevicesInfo info)
    {
        foreach (var obj in _wmi.Query("Win32_NetworkAdapter", 
            new[] { "Name", "AdapterType", "MACAddress", "NetConnectionStatus", "Speed" }))
        {
            var name = _wmi.GetValue<string>(obj, "Name", "") ?? "";
            var adapterType = _wmi.GetValue<string>(obj, "AdapterType", "") ?? "";
            
            // Skip virtual and internal adapters
            if (string.IsNullOrWhiteSpace(name) || 
                name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Loopback", StringComparison.OrdinalIgnoreCase))
                continue;

            var network = new NetworkDevice
            {
                Name = name,
                AdapterType = DetermineNetworkType(name, adapterType),
                MacAddress = _wmi.GetValue<string>(obj, "MACAddress", "") ?? "",
                IsConnected = _wmi.GetValue<ushort>(obj, "NetConnectionStatus", 0) == 2, // 2 = Connected
                Speed = FormatSpeed(_wmi.GetValue<ulong>(obj, "Speed", 0))
            };
            
            // Only add physical adapters
            if (!string.IsNullOrEmpty(network.MacAddress))
            {
                info.NetworkDevices.Add(network);
            }
        }
    }

    /// <summary>
    /// Determines network adapter type
    /// </summary>
    private string DetermineNetworkType(string name, string adapterType)
    {
        var nameLower = name.ToLower();
        
        if (nameLower.Contains("bluetooth"))
            return "Bluetooth";
        if (nameLower.Contains("wi-fi") || nameLower.Contains("wifi") || 
            nameLower.Contains("wireless") || nameLower.Contains("802.11"))
            return "WiFi";
        if (nameLower.Contains("ethernet") || nameLower.Contains("gigabit") || 
            nameLower.Contains("realtek") || adapterType.Contains("Ethernet"))
            return "Ethernet";
            
        return adapterType;
    }

    /// <summary>
    /// Formats network speed
    /// </summary>
    private string FormatSpeed(ulong speedBps)
    {
        if (speedBps == 0) return "";
        if (speedBps >= 1_000_000_000) return $"{speedBps / 1_000_000_000} Gbps";
        if (speedBps >= 1_000_000) return $"{speedBps / 1_000_000} Mbps";
        return $"{speedBps / 1_000} Kbps";
    }

    /// <summary>
    /// Detects webcam/camera
    /// </summary>
    private void DetectCamera(DevicesInfo info)
    {
        // Query for video capture devices
        var query = "SELECT * FROM Win32_PnPEntity WHERE Service='usbvideo'";
        
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(query);
            foreach (System.Management.ManagementObject obj in searcher.Get())
            {
                info.Camera = new CameraDevice
                {
                    Name = _wmi.GetValue<string>(obj, "Name", "Unknown Camera") ?? "Unknown Camera",
                    DeviceId = _wmi.GetValue<string>(obj, "DeviceID", "") ?? "",
                    Status = _wmi.GetValue<string>(obj, "Status", "Unknown") ?? "Unknown",
                    IsDetected = true
                };
                break; // Usually one camera
            }
        }
        catch { /* Ignore failures */ }

        // If no camera found via usbvideo, try alternate detection
        if (info.Camera == null)
        {
            var imageQuery = "SELECT * FROM Win32_PnPEntity WHERE Name LIKE '%Camera%' OR Name LIKE '%Webcam%'";
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(imageQuery);
                foreach (System.Management.ManagementObject obj in searcher.Get())
                {
                    var name = _wmi.GetValue<string>(obj, "Name", "") ?? "";
                    if (!name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                    {
                        info.Camera = new CameraDevice
                        {
                            Name = name,
                            DeviceId = _wmi.GetValue<string>(obj, "DeviceID", "") ?? "",
                            Status = _wmi.GetValue<string>(obj, "Status", "Unknown") ?? "Unknown",
                            IsDetected = true
                        };
                        break;
                    }
                }
            }
            catch { /* Ignore failures */ }
        }
    }

    /// <summary>
    /// Validates all devices are functioning
    /// </summary>
    public (bool IsHealthy, string Message) ValidateDevices(DevicesInfo info)
    {
        var issues = new List<string>();

        // Check for keyboard
        if (!info.HasKeyboard)
            issues.Add("No keyboard detected");
        else if (info.InputDevices.Where(d => d.Type == InputDeviceType.Keyboard).Any(d => !d.IsWorking))
            issues.Add("Keyboard not working properly");

        // Check for trackpad (laptops) or mouse
        if (!info.HasTrackpad && !info.HasMouse)
            issues.Add("No pointing device (trackpad/mouse) detected");

        // Check USB ports
        if (info.TotalUsbPorts == 0)
            issues.Add("No USB ports detected");
        else if (info.UsbPorts.Any(p => !p.IsWorking))
            issues.Add("Some USB ports not working");

        // Check camera
        if (info.Camera == null || !info.HasWebcam)
            issues.Add("No webcam detected");
        else if (!info.Camera.IsWorking)
            issues.Add("Webcam not working properly");

        // Check audio
        if (info.AudioDevices.Count == 0)
            issues.Add("No audio devices detected");
        else if (info.AudioDevices.Any(a => !a.IsWorking))
            issues.Add("Some audio devices not working");

        if (issues.Count > 0)
            return (false, string.Join("; ", issues));

        return (true, $"All devices OK - {info.InputDevices.Count} input, {info.TotalUsbPorts} USB ports, {info.Displays.Count} display(s)");
    }

    private Dictionary<string, DisplayId> GetMonitorIdMap()
    {
        var map = new Dictionary<string, DisplayId>(StringComparer.OrdinalIgnoreCase);

        foreach (var obj in _wmi.Query("WmiMonitorID", "root\\WMI"))
        {
            var instanceName = obj["InstanceName"]?.ToString() ?? "";
            var key = NormalizePnpId(instanceName);
            if (string.IsNullOrWhiteSpace(key))
                continue;

            var manufacturer = DecodeWmiString(obj["ManufacturerName"]);
            var productCode = DecodeWmiProductCode(obj["ProductCodeID"]);
            var serial = DecodeWmiString(obj["SerialNumberID"]);
            var partNumber = BuildDisplayPartNumber(manufacturer, productCode);

            map[key] = new DisplayId
            {
                ManufacturerCode = manufacturer,
                ProductCode = productCode,
                SerialNumber = serial,
                PartNumber = partNumber
            };
        }

        return map;
    }

    private static string NormalizePnpId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var normalized = value.Trim().Replace(@"\\", @"\").ToUpperInvariant();
        var underscoreIndex = normalized.IndexOf('_');
        if (underscoreIndex > 0)
            normalized = normalized.Substring(0, underscoreIndex);

        return normalized;
    }

    private static string DecodeWmiString(object? value)
    {
        if (value is ushort[] arr)
        {
            var chars = arr
                .TakeWhile(c => c != 0)
                .Select(c => (char)c)
                .ToArray();
            return new string(chars).Trim();
        }

        return value?.ToString()?.Trim() ?? "";
    }

    private static string DecodeWmiProductCode(object? value)
    {
        if (value is ushort u)
            return u.ToString();
        if (value is int i)
            return i.ToString();
        if (value is ushort[] arr && arr.Length > 0)
            return arr[0].ToString();

        return "";
    }

    private static string BuildDisplayPartNumber(string manufacturer, string productCode)
    {
        if (string.IsNullOrWhiteSpace(manufacturer) || string.IsNullOrWhiteSpace(productCode))
            return "";
        return $"{manufacturer}-{productCode}";
    }

    private sealed class DisplayId
    {
        public string ManufacturerCode { get; set; } = "";
        public string ProductCode { get; set; } = "";
        public string SerialNumber { get; set; } = "";
        public string PartNumber { get; set; } = "";
    }
}
#endif
