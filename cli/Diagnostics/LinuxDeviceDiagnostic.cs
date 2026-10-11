using System.Text.RegularExpressions;
using LaptopQC.Core.Abstractions;
using LaptopQC.Hardware.Models;

namespace Pramaan.CLI.Diagnostics;

public class LinuxDeviceDiagnostic : IDeviceDiagnostic
{
    public DevicesInfo GetInfo()
    {
        var info = new DevicesInfo();

        try { GetGpus(info); } catch { }
        try { GetDisplays(info); } catch { }
        try { GetAudio(info); } catch { }
        try { GetNetwork(info); } catch { }
        try { GetUsb(info); } catch { }
        try { GetCamera(info); } catch { }
        try { GetInputDevices(info); } catch { }

        return info;
    }

    public (bool IsHealthy, string Message) ValidateDevices(DevicesInfo info)
    {
        var issues = new List<string>();
        if (info.Displays.Count == 0) issues.Add("No displays detected");
        if (info.AudioDevices.Count == 0) issues.Add("No audio devices detected");
        if (!info.NetworkDevices.Any(n => n.IsConnected)) issues.Add("No active network connections");
        if (issues.Count > 0) return (false, string.Join("; ", issues));
        return (true, $"Devices OK: {info.Displays.Count} display(s), {info.AudioDevices.Count} audio, {(info.Camera != null ? 1 : 0)} camera(s)");
    }

    // ── GPU ──────────────────────────────────────────────────────────
    private static void GetGpus(DevicesInfo info)
    {
        var lspci = LinuxCommandRunner.TryRun("lspci", "");
        foreach (var line in lspci.Split('\n'))
        {
            if (!line.Contains("VGA compatible controller") && !line.Contains("3D controller") && !line.Contains("Display controller"))
                continue;

            // Extract GPU name: everything after the class descriptor
            var match = Regex.Match(line, @"(?:VGA compatible controller|3D controller|Display controller):\s+(.+)$");
            if (match.Success)
            {
                info.Gpus.Add(new GpuInfo
                {
                    Name = match.Groups[1].Value.Trim()
                });
            }
        }
    }

    // ── Displays ──────────────────────────────────────────────────────
    private static void GetDisplays(DevicesInfo info)
    {
        // Read from /sys/class/drm/
        var drmPath = "/sys/class/drm";
        if (!Directory.Exists(drmPath)) return;

        foreach (var connector in Directory.GetDirectories(drmPath))
        {
            var name = Path.GetFileName(connector);
            if (!name.Contains("-")) continue; // Skip card entries like "card0"

            var statusFile = Path.Combine(connector, "status");
            var status = LinuxCommandRunner.ReadFile(statusFile);
            if (status != "connected") continue;

            // Try to get resolution from edid or modes
            var modes = LinuxCommandRunner.ReadFile(Path.Combine(connector, "modes"));
            var firstMode = modes.Split('\n').FirstOrDefault(m => !string.IsNullOrWhiteSpace(m))?.Trim();

            int w = 0, h = 0;
            if (firstMode != null)
            {
                var resMatch = Regex.Match(firstMode, @"(\d+)x(\d+)");
                if (resMatch.Success)
                {
                    w = int.Parse(resMatch.Groups[1].Value);
                    h = int.Parse(resMatch.Groups[2].Value);
                }
            }

            // Determine connection type from the connector name (e.g. card0-HDMI-A-1 → HDMI)
            var connType = "Internal";
            if (name.Contains("HDMI", StringComparison.OrdinalIgnoreCase)) connType = "HDMI";
            else if (name.Contains("DP", StringComparison.OrdinalIgnoreCase)) connType = "DisplayPort";
            else if (name.Contains("VGA", StringComparison.OrdinalIgnoreCase)) connType = "VGA";
            else if (name.Contains("eDP", StringComparison.OrdinalIgnoreCase)) connType = "Internal (eDP)";

            info.Displays.Add(new DisplayDevice
            {
                Name = name,
                ConnectionType = connType,
                ScreenWidth = w,
                ScreenHeight = h,
                IsActive = true
            });
        }

        // Fallback: xrandr
        if (info.Displays.Count == 0)
        {
            var xrandr = LinuxCommandRunner.TryRun("xrandr", "--current");
            foreach (var line in xrandr.Split('\n'))
            {
                var match = Regex.Match(line, @"^(\S+)\s+connected\s+(?:primary\s+)?(\d+)x(\d+)");
                if (match.Success)
                {
                    info.Displays.Add(new DisplayDevice
                    {
                        Name = match.Groups[1].Value,
                        ScreenWidth = int.Parse(match.Groups[2].Value),
                        ScreenHeight = int.Parse(match.Groups[3].Value),
                        ConnectionType = match.Groups[1].Value.Contains("eDP") ? "Internal" : "External",
                        IsActive = true
                    });
                }
            }
        }
    }

    // ── Audio ──────────────────────────────────────────────────────────
    private static void GetAudio(DevicesInfo info)
    {
        // aplay -l lists ALSA playback devices
        var aplay = LinuxCommandRunner.TryRun("aplay", "-l");
        foreach (var line in aplay.Split('\n'))
        {
            var match = Regex.Match(line, @"card\s+\d+:\s+\w+\s+\[([^\]]+)\]");
            if (match.Success)
            {
                var name = match.Groups[1].Value.Trim();
                if (!info.AudioDevices.Any(a => a.Name == name))
                {
                    info.AudioDevices.Add(new AudioDevice
                    {
                        Name = name,
                        Status = "OK",
                        IsOutput = true
                    });
                }
            }
        }

        // pactl for PulseAudio/PipeWire
        if (info.AudioDevices.Count == 0)
        {
            var pactl = LinuxCommandRunner.TryRun("pactl", "list sinks short");
            foreach (var line in pactl.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                var parts = line.Trim().Split('\t');
                if (parts.Length >= 2)
                {
                    info.AudioDevices.Add(new AudioDevice
                    {
                        Name = parts[1],
                        Status = "OK",
                        IsOutput = true
                    });
                }
            }
        }
    }

    // ── Network ──────────────────────────────────────────────────────
    private static void GetNetwork(DevicesInfo info)
    {
        var ipLink = LinuxCommandRunner.TryRun("ip", "link show");
        var ipAddr = LinuxCommandRunner.TryRun("ip", "addr");

        // Parse interfaces
        var ifaceMatches = Regex.Matches(ipLink, @"^\d+:\s+(\w+):", RegexOptions.Multiline);
        foreach (Match m in ifaceMatches)
        {
            var ifaceName = m.Groups[1].Value;
            if (ifaceName == "lo") continue;

            // Determine type
            var adapterType = "Ethernet";
            if (ifaceName.StartsWith("wl")) adapterType = "WiFi";
            else if (ifaceName.StartsWith("ww") || ifaceName.StartsWith("usb")) adapterType = "Mobile";
            else if (ifaceName.StartsWith("bt")) adapterType = "Bluetooth";

            // Check if connected (has IP address)
            var inetMatch = Regex.Match(ipAddr, $@"inet\s+(\d+\.\d+\.\d+\.\d+).*{ifaceName}");
            bool connected = inetMatch.Success;

            // Get MAC
            var macMatch = Regex.Match(ipLink.Substring(m.Index), @"link/\w+\s+([0-9a-f:]{17})", RegexOptions.IgnoreCase);
            var mac = macMatch.Success ? macMatch.Groups[1].Value.ToUpperInvariant() : "";

            info.NetworkDevices.Add(new NetworkDevice
            {
                Name = ifaceName,
                AdapterType = adapterType,
                MacAddress = mac,
                IsConnected = connected
            });
        }
    }

    // ── USB ──────────────────────────────────────────────────────────
    private static void GetUsb(DevicesInfo info)
    {
        var lsusb = LinuxCommandRunner.TryRun("lsusb", "");
        foreach (var line in lsusb.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            // Bus 001 Device 001: ID 1d6b:0002 Linux Foundation 2.0 root hub
            var match = Regex.Match(line, @"ID\s+[\w:]+\s+(.+)$");
            if (match.Success)
            {
                var name = match.Groups[1].Value.Trim();
                info.ConnectedUsbDevices.Add(new UsbDevice
                {
                    Name = name,
                    Status = "OK"
                });
            }
        }

        // Count USB ports from /sys/bus/usb/devices/
        var usbSys = "/sys/bus/usb/devices";
        if (Directory.Exists(usbSys))
        {
            // USB 3.x controllers
            var usb3Count = Directory.GetDirectories(usbSys)
                .Count(d => LinuxCommandRunner.ReadFile(Path.Combine(d, "speed")) == "5000" ||
                            LinuxCommandRunner.ReadFile(Path.Combine(d, "speed")) == "10000");
            if (usb3Count > 0)
                info.UsbPorts.Add(new UsbPortInfo { UsbVersion = "3.0", PortCount = usb3Count, Status = "OK" });

            // USB 2.0 controllers
            var usb2Count = Directory.GetDirectories(usbSys)
                .Count(d => LinuxCommandRunner.ReadFile(Path.Combine(d, "speed")) == "480");
            if (usb2Count > 0)
                info.UsbPorts.Add(new UsbPortInfo { UsbVersion = "2.0", PortCount = usb2Count, Status = "OK" });
        }
    }

    // ── Camera ──────────────────────────────────────────────────────
    private static void GetCamera(DevicesInfo info)
    {
        // Check for /dev/video* devices
        var videoDevices = Directory.GetFiles("/dev", "video*")
            .OrderBy(f => f)
            .Take(1)
            .ToList();

        if (videoDevices.Count > 0)
        {
            // Try to get camera name from v4l2
            var v4l2Name = LinuxCommandRunner.TryRun("v4l2-ctl", $"--device={videoDevices[0]} --info");
            var nameMatch = Regex.Match(v4l2Name, @"Card type\s*:\s*(.+)$", RegexOptions.Multiline);
            var camName = nameMatch.Success ? nameMatch.Groups[1].Value.Trim() : "Webcam";

            info.Camera = new CameraDevice
            {
                Name = camName,
                IsDetected = true,
                Status = "OK"
            };
        }
    }

    // ── Input Devices ─────────────────────────────────────────────
    private static void GetInputDevices(DevicesInfo info)
    {
        // /proc/bus/input/devices lists all input devices
        var inputDevices = LinuxCommandRunner.ReadFile("/proc/bus/input/devices");
        var blocks = Regex.Split(inputDevices, @"\n\n");

        foreach (var block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block)) continue;

            var nameMatch = Regex.Match(block, @"^N: Name=""(.+)""", RegexOptions.Multiline);
            if (!nameMatch.Success) continue;
            var name = nameMatch.Groups[1].Value.Trim();

            var handlersMatch = Regex.Match(block, @"^H: Handlers=(.+)$", RegexOptions.Multiline);
            var handlers = handlersMatch.Success ? handlersMatch.Groups[1].Value : "";

            InputDeviceType? devType = null;
            if (name.Contains("keyboard", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Keyboard", StringComparison.OrdinalIgnoreCase))
                devType = InputDeviceType.Keyboard;
            else if (name.Contains("TouchPad", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("Trackpad", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("Synaptics", StringComparison.OrdinalIgnoreCase))
                devType = InputDeviceType.Trackpad;
            else if (name.Contains("Mouse", StringComparison.OrdinalIgnoreCase) && handlers.Contains("mouse"))
                devType = InputDeviceType.Mouse;
            else if (name.Contains("Touch", StringComparison.OrdinalIgnoreCase))
                devType = InputDeviceType.Touchscreen;

            if (devType.HasValue)
            {
                info.InputDevices.Add(new InputDevice
                {
                    Name = name,
                    Type = devType.Value,
                    Status = "OK"
                });
            }
        }
    }
}
