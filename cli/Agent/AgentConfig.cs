using System.Text.Json;

namespace Pramaan.CLI.Agent;

public sealed class AgentConfig
{
    public string ApiUrl { get; set; } = "https://pramaan-dashboard.gadgetguruz.com/api";
    public string? LicenseKey { get; set; }
    public string? MachineSerial { get; set; }
    public string? MacAddress { get; set; }
    public string? ComputerName { get; set; }

    public string[] ServiceUnits { get; set; } = Array.Empty<string>();
    public string[] ReachabilityUrls { get; set; } = Array.Empty<string>();
    public string DnsTestDomain { get; set; } = "google.com";

    // Thresholds
    public int DiskWarnPercent { get; set; } = 80;
    public int DiskCritPercent { get; set; } = 90;
    public int MemWarnPercent { get; set; } = 85;
    public int MemCritPercent { get; set; } = 95;
    public double LoadWarnPerCore { get; set; } = 1.0;
    public double LoadCritPerCore { get; set; } = 2.0;

    public static string GetDefaultConfigPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".pramaan", "agent.json");
    }

    public static AgentConfig Load(string? path = null)
    {
        path ??= GetDefaultConfigPath();
        try
        {
            if (!File.Exists(path)) return new AgentConfig();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AgentConfig>(json) ?? new AgentConfig();
        }
        catch
        {
            return new AgentConfig();
        }
    }

    public void Save(string? path = null)
    {
        path ??= GetDefaultConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}

