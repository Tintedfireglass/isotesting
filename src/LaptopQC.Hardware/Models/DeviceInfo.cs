namespace LaptopQC.Hardware.Models;

/// <summary>
/// Aggregated device information for all peripherals and ports
/// </summary>
public class DevicesInfo
{
    public List<InputDevice> InputDevices { get; set; } = new();
    public List<UsbPortInfo> UsbPorts { get; set; } = new();
    public List<UsbDevice> ConnectedUsbDevices { get; set; } = new();
    public List<DisplayDevice> Displays { get; set; } = new();
    public List<GpuInfo> Gpus { get; set; } = new();
    public List<AudioDevice> AudioDevices { get; set; } = new();
    public List<NetworkDevice> NetworkDevices { get; set; } = new();
    public CameraDevice? Camera { get; set; }
    
    // Summary counts
    public int TotalUsbPorts => UsbPorts.Sum(p => p.PortCount);
    public int Usb3Ports => UsbPorts.Where(p => p.UsbVersion.Contains("3.")).Sum(p => p.PortCount);
    public int Usb2Ports => UsbPorts.Where(p => p.UsbVersion.Contains("2.")).Sum(p => p.PortCount);
    public bool HasKeyboard => InputDevices.Any(d => d.Type == InputDeviceType.Keyboard);
    public bool HasTrackpad => InputDevices.Any(d => d.Type == InputDeviceType.Trackpad);
    public bool HasMouse => InputDevices.Any(d => d.Type == InputDeviceType.Mouse);
    public bool HasWebcam => Camera != null && Camera.IsDetected;
}

/// <summary>
/// Input device (keyboard, trackpad, mouse)
/// </summary>
public class InputDevice
{
    public string Name { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public InputDeviceType Type { get; set; }
    public string Status { get; set; } = "";
    public bool IsWorking => Status.Equals("OK", StringComparison.OrdinalIgnoreCase);
    public string Description { get; set; } = "";
}

public enum InputDeviceType
{
    Keyboard,
    Trackpad,
    Mouse,
    Touchscreen,
    Other
}

/// <summary>
/// USB controller/port information
/// </summary>
public class UsbPortInfo
{
    public string ControllerName { get; set; } = "";
    public string UsbVersion { get; set; } = "";  // "2.0", "3.0", "3.1", "3.2"
    public int PortCount { get; set; } = 1;
    public string DeviceId { get; set; } = "";
    public string Status { get; set; } = "";
    public bool IsWorking => Status.Equals("OK", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Connected USB device
/// </summary>
public class UsbDevice
{
    public string Name { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Status { get; set; } = "";
    public bool IsConnected => Status.Equals("OK", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Display/monitor information
/// </summary>
public class DisplayDevice
{
    public string Name { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string ConnectionType { get; set; } = "";  // Internal, HDMI, VGA, DisplayPort
    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }
    public string Resolution => $"{ScreenWidth}x{ScreenHeight}";
    public bool IsActive { get; set; }
    public string ManufacturerCode { get; set; } = "";
    public string ProductCode { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string PartNumber { get; set; } = "";
}

/// <summary>
/// Audio device information
/// </summary>
public class AudioDevice
{
    public string Name { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string Status { get; set; } = "";
    public bool IsOutput { get; set; }  // Speaker/headphone
    public bool IsInput { get; set; }   // Microphone
    public bool IsWorking => Status.Equals("OK", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Network adapter information
/// </summary>
public class NetworkDevice
{
    public string Name { get; set; } = "";
    public string AdapterType { get; set; } = "";  // Ethernet, WiFi, Bluetooth
    public string MacAddress { get; set; } = "";
    public bool IsConnected { get; set; }
    public string Speed { get; set; } = "";
}

/// <summary>
/// Webcam/camera information
/// </summary>
public class CameraDevice
{
    public string Name { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string Status { get; set; } = "";
    public bool IsDetected { get; set; }
    public bool IsWorking => Status.Equals("OK", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// GPU / Video Controller information
/// </summary>
public class GpuInfo
{
    public string Name { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string DriverVersion { get; set; } = "";
    public double MemoryGB { get; set; }
    public string Status { get; set; } = "";
    public int CurrentResX { get; set; }
    public int CurrentResY { get; set; }
    public uint CurrentRefreshRate { get; set; }
}
