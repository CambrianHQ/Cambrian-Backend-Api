namespace Cambrian.Domain.Entities;

public sealed class MediaReconciliationRun
{
    public Guid Id { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string Status { get; set; } = "running";
    public bool RemediationEnabled { get; set; }
    public int TracksInspected { get; set; }
    public int ObjectsInspected { get; set; }
    public int FindingCount { get; set; }
    public int UnresolvedPublishedTrackFailures { get; set; }
    public string? FailureCode { get; set; }
    public ICollection<MediaReconciliationFinding> Findings { get; set; } = new List<MediaReconciliationFinding>();
}

public sealed class MediaReconciliationFinding
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid? TrackId { get; set; }
    public string FindingType { get; set; } = "";
    public string Severity { get; set; } = "warning";
    public string? ObjectKey { get; set; }
    public string Detail { get; set; } = "";
    public string Resolution { get; set; } = "operator_action_required";
    public DateTime CreatedAtUtc { get; set; }
    public MediaReconciliationRun Run { get; set; } = null!;
}
