namespace LaptopQC.Core.Models;

/// <summary>
/// PRAMAAN scoring output. Produced by PramaanScoringEngine for every graded device.
/// </summary>
public class PramaanResult
{
    /// <summary>Overall health score 0–100.</summary>
    public int OverallHealthScore { get; set; }

    /// <summary>Grade band: A+, A, B, C, or Reject.</summary>
    public string GradeBand { get; set; } = "Reject";

    /// <summary>Per-category sub-scores (0–100 each).</summary>
    public Dictionary<string, int> CategoryScores { get; set; } = new();

    /// <summary>
    /// Per-category risk flags. True = score below risk threshold.
    /// </summary>
    public Dictionary<string, bool> RiskFlags { get; set; } = new();

    /// <summary>True if any risk flag is raised.</summary>
    public bool HasRisk => RiskFlags.Values.Any(v => v);

    /// <summary>Scoring engine version that produced this result.</summary>
    public string AlgorithmVersion { get; set; } = "";

    /// <summary>Timestamp of score generation (UTC).</summary>
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Human-readable label for the grade band.</summary>
    public string GradeLabel => GradeBand switch
    {
        "A+" => "Certified Premium",
        "A"  => "Certified",
        "B"  => "Good Condition",
        "C"  => "Acceptable",
        "Reject" => "Not Certified",
        _ => "Unknown"
    };
}
