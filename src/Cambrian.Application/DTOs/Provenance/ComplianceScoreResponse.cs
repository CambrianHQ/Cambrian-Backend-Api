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

    /// <summary>
    /// Backend-owned release/compliance checklist rows. These are additive; legacy
    /// clients can continue to rely on <see cref="Score"/> and <see cref="Checks"/>.
    /// </summary>
    public List<ComplianceChecklistItemDto> ChecklistItems { get; set; } = new();

    /// <summary>
    /// Maximum score attainable without paid verification. Authorship Records are
    /// optional paid verification today, so the free checklist can still reach 100.
    /// </summary>
    public int FreeMaxScore { get; set; } = 100;
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

/// <summary>One backend-owned checklist row for frontend release-readiness UI.</summary>
public sealed class ComplianceChecklistItemDto
{
    /// <summary>Stable snake_case item key.</summary>
    public string Key { get; set; } = "";

    public string Label { get; set; } = "";

    /// <summary>
    /// Outcome: <c>complete</c> | <c>incomplete</c> | <c>optional</c> |
    /// <c>paid_required</c> | <c>optional_paid_verification</c>.
    /// </summary>
    public string Status { get; set; } = "incomplete";

    public string Explanation { get; set; } = "";

    /// <summary>Frontend section hint, when the UI can route directly to it.</summary>
    public string? TargetSection { get; set; }

    /// <summary>Frontend anchor hint, when the UI can route directly to it.</summary>
    public string? AnchorId { get; set; }

    /// <summary>True only when a release-readiness tier intentionally requires payment.</summary>
    public bool IsPaidRequirement { get; set; }

    /// <summary>Completion timestamp when the backing source exposes one.</summary>
    public DateTime? CompletedAt { get; set; }
}
