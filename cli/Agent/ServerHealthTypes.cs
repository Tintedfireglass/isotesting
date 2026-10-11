namespace Pramaan.CLI.Agent;

public enum ServerHealthStatus
{
    Ok,
    Degraded,
    Critical,
    Unknown
}

public sealed class ServerHealthCheck
{
    public string Name { get; set; } = "";
    public ServerHealthStatus Status { get; set; } = ServerHealthStatus.Unknown;
    public string? Summary { get; set; }
    public Dictionary<string, double>? Metrics { get; set; }
    public List<string>? Details { get; set; }
}

public sealed class ServerHealthReport
{
    public string SchemaVersion { get; set; } = "1";
    public DateTime CollectedAtUtc { get; set; } = DateTime.UtcNow;
    public string? AgentVersion { get; set; }
    public ServerHealthStatus OverallStatus { get; set; } = ServerHealthStatus.Unknown;
    public List<ServerHealthCheck> Checks { get; set; } = new();
}

