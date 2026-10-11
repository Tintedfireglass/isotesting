using System.Text.RegularExpressions;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Pramaan.CLI.Diagnostics;

/// <summary>
/// Ethernet port diagnostic for Linux - tests connectivity, link speed, and cable detection
/// </summary>
public class LinuxEthernetDiagnostic
{
    public class EthernetTestResult
    {
        public List<EthernetPort> DetectedPorts { get; set; } = new();
        public bool HasWorkingPort => DetectedPorts.Any(p => p.IsConnected);
        public int TotalPorts => DetectedPorts.Count;
        public int ConnectedPorts => DetectedPorts.Count(p => p.IsConnected);
        public string Summary => $"{ConnectedPorts}/{TotalPorts} Ethernet ports connected";

        // Interactive test tracking (mirrors USB UsbPortTestResult)
        public List<EthernetPortTest> TestedPorts { get; set; } = new();
        public bool AllPortsWorking => TestedPorts.Count > 0 && TestedPorts.All(p => p.Passed);
        public int WorkingPortsCount => TestedPorts.Count(p => p.Passed);
        public string InteractiveSummary => $"{WorkingPortsCount}/{TestedPorts.Count} ports working";
    }

    /// <summary>
    /// Per-port result from an interactive cable-plug test (mirrors USB PortTest)
    /// </summary>
    public class EthernetPortTest
    {
        public string PortName { get; set; } = "";       // e.g. "enp1s0"
        public bool Passed { get; set; }
        public string LinkSpeed { get; set; } = "";      // e.g. "1000Mb/s"
        public string IpAddress { get; set; } = "";
        public string MacAddress { get; set; } = "";
        public DateTime TestedAt { get; set; } = DateTime.Now;
    }

    public class EthernetPort
    {
        public string InterfaceName { get; set; } = "";
        public string MacAddress { get; set; } = "";
        public bool IsConnected { get; set; }
        public bool CableDetected { get; set; }
        public string LinkSpeed { get; set; } = "";  // e.g., "1000Mb/s", "100Mb/s"
        public string Duplex { get; set; } = "";      // "full", "half"
        public string IpAddress { get; set; } = "";
        public string Driver { get; set; } = "";
        public bool Passed => IsConnected && CableDetected;
    }

    /// <summary>
    /// Detects all Ethernet interfaces on the system
    /// </summary>
    public List<string> DetectEthernetInterfaces()
    {
        var interfaces = new List<string>();

        try
        {
            // Method 1: Use 'ip link' to find Ethernet interfaces
            var ipLink = LinuxCommandRunner.TryRun("ip", "link show");
            var matches = Regex.Matches(ipLink, @"^\d+:\s+([a-z0-9]+):", RegexOptions.Multiline);

            foreach (Match match in matches)
            {
                var ifaceName = match.Groups[1].Value;
                
                // Filter for Ethernet interfaces (exclude lo, wl*, docker*, br*, virbr*)
                if (ifaceName == "lo" || 
                    ifaceName.StartsWith("wl") || 
                    ifaceName.StartsWith("ww") ||
                    ifaceName.StartsWith("docker") ||
                    ifaceName.StartsWith("br") ||
                    ifaceName.StartsWith("virbr") ||
                    ifaceName.StartsWith("veth"))
                {
                    continue;
                }

                interfaces.Add(ifaceName);
            }

            // Method 2: Check /sys/class/net for Ethernet devices
            if (interfaces.Count == 0 && Directory.Exists("/sys/class/net"))
            {
                foreach (var netDir in Directory.GetDirectories("/sys/class/net"))
                {
                    var ifaceName = Path.GetFileName(netDir);
                    
                    // Check if it's an Ethernet device by looking at the type
                    var typePath = Path.Combine(netDir, "type");
                    var type = LinuxCommandRunner.ReadFile(typePath).Trim();
                    
                    // Type 1 = Ethernet
                    if (type == "1" && 
                        !ifaceName.StartsWith("wl") && 
                        !ifaceName.StartsWith("lo") &&
                        !ifaceName.StartsWith("docker") &&
                        !ifaceName.StartsWith("br") &&
                        !ifaceName.StartsWith("virbr"))
                    {
                        interfaces.Add(ifaceName);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Ethernet detection error: {ex.Message}");
        }

        return interfaces.Distinct().OrderBy(i => i).ToList();
    }

    /// <summary>
    /// Gets detailed information about a specific Ethernet interface
    /// </summary>
    public EthernetPort GetPortInfo(string interfaceName)
    {
        var port = new EthernetPort { InterfaceName = interfaceName };

        try
        {
            // Get MAC address
            var macPath = $"/sys/class/net/{interfaceName}/address";
            port.MacAddress = LinuxCommandRunner.ReadFile(macPath).Trim().ToUpperInvariant();

            // Check carrier (cable connection)
            var carrierPath = $"/sys/class/net/{interfaceName}/carrier";
            var carrier = LinuxCommandRunner.ReadFile(carrierPath).Trim();
            port.CableDetected = carrier == "1";

            // Get operational state
            var operstatePath = $"/sys/class/net/{interfaceName}/operstate";
            var operstate = LinuxCommandRunner.ReadFile(operstatePath).Trim();
            port.IsConnected = operstate == "up";

            // Get link speed and duplex using ethtool
            try
            {
                var ethtool = LinuxCommandRunner.TryRun("ethtool", interfaceName);
                
                var speedMatch = Regex.Match(ethtool, @"Speed:\s*(\d+)Mb/s", RegexOptions.IgnoreCase);
                if (speedMatch.Success)
                {
                    port.LinkSpeed = $"{speedMatch.Groups[1].Value}Mb/s";
                }

                var duplexMatch = Regex.Match(ethtool, @"Duplex:\s*(\w+)", RegexOptions.IgnoreCase);
                if (duplexMatch.Success)
                {
                    port.Duplex = duplexMatch.Groups[1].Value.ToLowerInvariant();
                }

                // Get driver info
                var driverMatch = Regex.Match(ethtool, @"driver:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                if (driverMatch.Success)
                {
                    port.Driver = driverMatch.Groups[1].Value.Trim();
                }
            }
            catch
            {
                // ethtool might require root or not be installed
                port.LinkSpeed = "Unknown";
                port.Duplex = "Unknown";
            }

            // Get IP address
            try
            {
                var ipAddr = LinuxCommandRunner.TryRun("ip", $"addr show {interfaceName}");
                var ipMatch = Regex.Match(ipAddr, @"inet\s+([\d\.]+)");
                if (ipMatch.Success)
                {
                    port.IpAddress = ipMatch.Groups[1].Value;
                }
            }
            catch { }

        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error getting port info for {interfaceName}: {ex.Message}");
        }

        return port;
    }

    /// <summary>
    /// Runs a full diagnostic scan of all Ethernet ports
    /// </summary>
    public EthernetTestResult RunDiagnostic()
    {
        var result = new EthernetTestResult();

        var interfaces = DetectEthernetInterfaces();
        
        foreach (var iface in interfaces)
        {
            var portInfo = GetPortInfo(iface);
            result.DetectedPorts.Add(portInfo);
        }

        return result;
    }

    /// <summary>
    /// Interactive test that waits for user to plug in Ethernet cable
    /// </summary>
    public async Task<bool> InteractiveCableTestAsync(
        string interfaceName,
        Action<string>? statusCallback = null,
        int timeoutSeconds = 30)
    {
        statusCallback?.Invoke($"Waiting for cable connection on {interfaceName}...");
        
        var startTime = DateTime.Now;
        
        while ((DateTime.Now - startTime).TotalSeconds < timeoutSeconds)
        {
            var port = GetPortInfo(interfaceName);
            
            if (port.CableDetected && port.IsConnected)
            {
                statusCallback?.Invoke($"✓ Cable connected! Link: {port.LinkSpeed} {port.Duplex}");
                return true;
            }

            await Task.Delay(1000);
        }

        statusCallback?.Invoke($"✗ Timeout: No cable detected on {interfaceName}");
        return false;
    }

    /// <summary>
    /// Tests network connectivity by pinging a reliable host
    /// </summary>
    public async Task<(bool Success, long LatencyMs, string Message)> TestConnectivityAsync(
        string interfaceName,
        string targetHost = "8.8.8.8")
    {
        try
        {
            // Use ping with specific interface binding
            var ping = new Ping();
            var reply = await ping.SendPingAsync(targetHost, 3000);

            if (reply.Status == IPStatus.Success)
            {
                return (true, reply.RoundtripTime, $"Ping successful: {reply.RoundtripTime}ms");
            }
            else
            {
                return (false, 0, $"Ping failed: {reply.Status}");
            }
        }
        catch (Exception ex)
        {
            return (false, 0, $"Connectivity test failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Performs a basic network throughput test
    /// </summary>
    public async Task<(bool Success, double SpeedMbps, string Message)> TestThroughputAsync(
        string interfaceName,
        Action<string>? statusCallback = null)
    {
        try
        {
            statusCallback?.Invoke($"Testing network throughput on {interfaceName}...");

            // Simple HTTP download test to measure throughput
            // Using a small file from a reliable CDN
            var testUrl = "http://speedtest.ftp.otenet.gr/files/test1Mb.db";
            var testSizeMB = 1.0;

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            
            var startTime = DateTime.Now;
            var response = await client.GetAsync(testUrl);
            var content = await response.Content.ReadAsByteArrayAsync();
            var elapsed = (DateTime.Now - startTime).TotalSeconds;

            if (elapsed > 0)
            {
                var speedMbps = (testSizeMB * 8) / elapsed; // Convert MB to Mbits and divide by seconds
                statusCallback?.Invoke($"Throughput: {speedMbps:F2} Mbps");
                return (true, speedMbps, $"Download speed: {speedMbps:F2} Mbps");
            }

            return (false, 0, "Invalid test result");
        }
        catch (Exception ex)
        {
            return (false, 0, $"Throughput test failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Quick validation of Ethernet functionality
    /// </summary>
    public (bool IsHealthy, string Message) QuickValidation()
    {
        try
        {
            var result = RunDiagnostic();

            if (result.TotalPorts == 0)
            {
                return (false, "No Ethernet ports detected");
            }

            if (!result.HasWorkingPort)
            {
                return (false, $"No active Ethernet connection ({result.TotalPorts} ports detected)");
            }

            var connectedPort = result.DetectedPorts.First(p => p.IsConnected);
            return (true, $"Ethernet OK: {connectedPort.InterfaceName} @ {connectedPort.LinkSpeed}");
        }
        catch (Exception ex)
        {
            return (false, $"Ethernet check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Full interactive test of all Ethernet ports (legacy — non-interactive cable-plug style)
    /// </summary>
    public async Task<EthernetTestResult> RunFullInteractiveTestAsync(
        Action<string>? statusCallback = null)
    {
        var result = RunDiagnostic();

        statusCallback?.Invoke($"Found {result.TotalPorts} Ethernet port(s)");

        foreach (var port in result.DetectedPorts)
        {
            statusCallback?.Invoke($"\nTesting {port.InterfaceName}:");
            statusCallback?.Invoke($"  MAC: {port.MacAddress}");
            statusCallback?.Invoke($"  Link Speed: {port.LinkSpeed}");
            statusCallback?.Invoke($"  Duplex: {port.Duplex}");
            statusCallback?.Invoke($"  Cable: {(port.CableDetected ? "Connected" : "Not Connected")}");
            statusCallback?.Invoke($"  Status: {(port.IsConnected ? "UP" : "DOWN")}");

            if (port.IsConnected)
            {
                statusCallback?.Invoke($"  IP: {port.IpAddress}");

                // Test connectivity
                var (success, latency, message) = await TestConnectivityAsync(port.InterfaceName);
                statusCallback?.Invoke($"  Ping: {message}");
            }
        }

        return result;
    }

    // ── Interactive cable-plug test (mirrors LinuxUsbPortDiagnostic) ─────────

    /// <summary>
    /// Interactive test that guides the technician to plug a cable into each
    /// detected Ethernet port and waits for carrier signal (mirrors USB flow).
    /// </summary>
    public async Task<EthernetTestResult> RunInteractiveTestAsync(
        Action<string>? statusCallback = null,
        int timeoutSeconds = 30)
    {
        var result = new EthernetTestResult();

        // Populate DetectedPorts so QuickValidation-style info is still available
        var interfaces = DetectEthernetInterfaces();
        foreach (var iface in interfaces)
            result.DetectedPorts.Add(GetPortInfo(iface));

        statusCallback?.Invoke($"Detected {interfaces.Count} Ethernet port(s)");

        if (interfaces.Count == 0)
        {
            statusCallback?.Invoke("No Ethernet interfaces found. Skipping test.");
            return result;
        }

        for (int i = 0; i < interfaces.Count; i++)
        {
            var iface = interfaces[i];
            statusCallback?.Invoke($"Testing Ethernet Port {i + 1}/{interfaces.Count} ({iface})...");
            var portTest = await TestSinglePortAsync(iface, i + 1, interfaces.Count, statusCallback, timeoutSeconds);
            result.TestedPorts.Add(portTest);
        }

        return result;
    }

    /// <summary>
    /// Tests a single Ethernet port by prompting the technician to plug in a
    /// cable and waiting for the OS carrier signal (mirrors USB TestSinglePortAsync).
    /// </summary>
    private async Task<EthernetPortTest> TestSinglePortAsync(
        string interfaceName,
        int portIndex,
        int totalPorts,
        Action<string>? statusCallback = null,
        int timeoutSeconds = 30)
    {
        statusCallback?.Invoke($"Plug a live Ethernet cable into port {portIndex}/{totalPorts} ({interfaceName})... ({timeoutSeconds}s timeout)");

        var portInfo = await WaitForCableAsync(interfaceName, timeoutSeconds);

        return new EthernetPortTest
        {
            PortName     = interfaceName,
            Passed       = portInfo != null,
            LinkSpeed    = portInfo?.LinkSpeed ?? "",
            IpAddress    = portInfo?.IpAddress ?? "",
            MacAddress   = portInfo?.MacAddress ?? "",
            TestedAt     = DateTime.Now
        };
    }

    /// <summary>
    /// Polls the OS carrier file for the given interface until a cable is detected
    /// or the timeout is reached (mirrors USB WaitForUsbDeviceAsync).
    /// Returns populated EthernetPort on success, null on timeout.
    /// </summary>
    private async Task<EthernetPort?> WaitForCableAsync(
        string interfaceName,
        int timeoutSeconds = 30)
    {
        var startTime = DateTime.Now;

        while ((DateTime.Now - startTime).TotalSeconds < timeoutSeconds)
        {
            await Task.Delay(500);

            var carrierPath = $"/sys/class/net/{interfaceName}/carrier";
            var carrier = LinuxCommandRunner.ReadFile(carrierPath).Trim();

            if (carrier == "1")
            {
                // Cable detected — gather full port info
                return GetPortInfo(interfaceName);
            }
        }

        return null; // Timeout
    }
}
