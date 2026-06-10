namespace Cambrian.Application.DTOs.Readiness;

/// <summary>Result of POST /api/tracks/{id}/release-ready.</summary>
public sealed class StartReleaseJobResult
{
    public Guid JobId { get; init; }

    /// <summary>False when an existing live job for the same audio was returned instead of charging again.</summary>
    public bool Created { get; init; }

    /// <summary>Set when the request was idempotently coalesced onto an existing job.</summary>
    public string? Warning { get; init; }
}

/// <summary>Response shape for GET /api/jobs/{id} (contract: status, stage, artifacts).</summary>
public sealed class ReleaseJobResponse
{
    public Guid Id { get; init; }
    public Guid? TrackId { get; init; }

    /// <summary>queued | processing | awaiting_approval | done | failed.</summary>
    public string Status { get; init; } = "";

    /// <summary>Current stage: mastering | metadata | cover | disclosure | provenance.</summary>
    public string? Stage { get; init; }

    /// <summary>Persisted stage transitions, oldest first.</summary>
    public IReadOnlyList<ReleaseStageEntry> Stages { get; init; } = Array.Empty<ReleaseStageEntry>();

    /// <summary>Signed-URL artifacts produced so far.</summary>
    public IReadOnlyList<ReleaseArtifact> Artifacts { get; init; } = Array.Empty<ReleaseArtifact>();

    public string? Error { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
}

public sealed class ReleaseStageEntry
{
    public string Stage { get; set; } = "";

    /// <summary>started | completed | failed.</summary>
    public string Status { get; set; } = "";

    public DateTime At { get; set; }
    public string? Detail { get; set; }
}

public sealed class ReleaseArtifact
{
    /// <summary>master_wav | master_mp3 | disclosure.</summary>
    public string Kind { get; init; } = "";

    /// <summary>Time-limited signed URL.</summary>
    public string Url { get; init; } = "";
}
