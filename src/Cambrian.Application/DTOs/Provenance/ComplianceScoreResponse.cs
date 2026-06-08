namespace Cambrian.Application.DTOs.Provenance;

/// <summary>
/// Compliance score for <c>GET /api/tracks/{id}/compliance-score</c>. A deterministic
/// 0–100 score derived from <see cref="Checks"/>; the per-check breakdown explains it.
/// </summary>
public sealed class ComplianceScoreResponse
{
    /// <summary>Overall score, 0–100.</summary>
    public int Score { get; set; }

    public List<ComplianceCheck> Checks { get; set; } = new();
}

/// <summary>One concrete compliance rule and its outcome.</summary>
public sealed class ComplianceCheck
{
    /// <summary>Stable camelCase check key (e.g. <c>provenanceAnchored</c>).</summary>
    public string Name { get; set; } = "";

    /// <summary>Outcome: <c>pass</c> | <c>warn</c> | <c>fail</c>.</summary>
    public string Status { get; set; } = "fail";

    /// <summary>Human-readable explanation of the outcome.</summary>
    public string Detail { get; set; } = "";
}
