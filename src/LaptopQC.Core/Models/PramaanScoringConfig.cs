using System.Text.Json.Serialization;

namespace LaptopQC.Core.Models;

/// <summary>
/// Database-driven scoring configuration for the PRAMAAN engine.
/// All scoring logic reads from this config — nothing is hardcoded.
/// </summary>
public class PramaanScoringConfig
{
    /// <summary>Current config version. Stored with every score for audit trail.</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.3";

    /// <summary>
    /// Category weights (must sum to 1.0).
    /// Keys: storage, thermal, battery, cpu_ram, physical_ports, repair_modifier
    /// </summary>
    [JsonPropertyName("weights")]
    public Dictionary<string, double> Weights { get; set; } = new()
    {
        ["storage"]         = 0.25,
        ["thermal"]         = 0.20,
        ["battery"]         = 0.25,
        ["cpu_ram"]          = 0.15,
        ["physical_ports"]  = 0.05,
        ["repair_modifier"] = 0.10,
    };

    /// <summary>
    /// Grade band thresholds. Score >= threshold gets that grade.
    /// Evaluated top-down: first match wins.
    /// </summary>
    [JsonPropertyName("gradeBands")]
    public List<GradeBand> GradeBands { get; set; } = new()
    {
        new() { Grade = "A+",     MinScore = 90 },
        new() { Grade = "A",      MinScore = 80 },
        new() { Grade = "B",      MinScore = 70 },
        new() { Grade = "C",      MinScore = 60 },
        new() { Grade = "D",      MinScore = 50 },
        new() { Grade = "Reject", MinScore = 0  },
    };

    /// <summary>
    /// Risk flag thresholds per category.
    /// If a category score falls below this value, the risk flag is raised.
    /// </summary>
    [JsonPropertyName("riskThresholds")]
    public Dictionary<string, int> RiskThresholds { get; set; } = new()
    {
        ["storage"]         = 40,
        ["thermal"]         = 40,
        ["battery"]         = 35,
        ["cpu_ram"]          = 30,
        ["physical_ports"]  = 50,
        ["repair_modifier"] = 50,
    };

    /// <summary>Default repair modifier score when no Saarthi data is available (0–100).</summary>
    [JsonPropertyName("defaultRepairModifierScore")]
    public int DefaultRepairModifierScore { get; set; } = 100;

    /// <summary>Certification validity in days (used by future phases).</summary>
    [JsonPropertyName("certificationValidityDays")]
    public int CertificationValidityDays { get; set; } = 180;

    /// <summary>Resolve grade band from a 0–100 score.</summary>
    public string ScoreToGrade(int score)
    {
        foreach (var band in GradeBands.OrderByDescending(b => b.MinScore))
        {
            if (score >= band.MinScore)
                return band.Grade;
        }
        return "Reject";
    }
}

/// <summary>A single grade band entry.</summary>
public class GradeBand
{
    [JsonPropertyName("grade")]
    public string Grade { get; set; } = "";
    
    [JsonPropertyName("minScore")]
    public int MinScore { get; set; }
}
