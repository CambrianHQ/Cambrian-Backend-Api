namespace Cambrian.Domain.Entities;

/// <summary>
/// A Release Ready mastering job. One row per submitted master; the in-process
/// <c>MasteringWorker</c> advances <see cref="Status"/>
/// queued → processing → done|failed (with a single retry).
///
/// <para>
/// Credits: <see cref="ChargedAt"/> is the credit ledger. It is set exactly once
/// when a credit is consumed for this job (on submit for ffmpeg engines, on
/// approval for preview engines). Remaining monthly credits are derived by
/// counting jobs charged in the current calendar month — so there is no separate
/// balance to drift and no rollover by construction.
/// </para>
/// </summary>
public class MasteringJob
{
    public Guid Id { get; set; }

    /// <summary>Owner — FK to AspNetUsers.Id.</summary>
    public string CreatorId { get; set; } = "";

    /// <summary>Optional catalog track this master is for (null for ad-hoc uploads).</summary>
    public Guid? TrackId { get; set; }

    /// <summary>Engine that ran the job: <c>ffmpeg</c> | <c>tonn</c>.</summary>
    public string Engine { get; set; } = "ffmpeg";

    /// <summary>
    /// <c>mastering</c> — classic upload-and-master flow; <c>release_pipeline</c> —
    /// track-based Release Ready job that also runs the Metadata → Cover →
    /// Disclosure → Provenance stages after mastering.
    /// </summary>
    public string Kind { get; set; } = "mastering";

    /// <summary>
    /// SHA-256 hex of the source audio at submit time. Release-pipeline idempotency:
    /// one charged job per (TrackId, ContentHash) — re-running unchanged audio warns
    /// instead of double-charging.
    /// </summary>
    public string? ContentHash { get; set; }

    /// <summary>Current pipeline stage: mastering | metadata | cover | disclosure | provenance (null for classic jobs).</summary>
    public string? Stage { get; set; }

    /// <summary>JSON array of stage transitions <c>[{stage, status, at, detail?}]</c> for GET /api/jobs/{id}.</summary>
    public string? StageHistoryJson { get; set; }

    /// <summary>queued | processing | awaiting_approval | done | failed.</summary>
    public string Status { get; set; } = "queued";

    /// <summary>Storage key of the uploaded source audio — read-only, never modified.</summary>
    public string SourceKey { get; set; } = "";

    public string? SourceFileName { get; set; }

    /// <summary>Validated cover-art object used for embedding into release exports.</summary>
    public string? CoverArtKey { get; set; }

    /// <summary>Mastered 44.1k/16-bit WAV output (new key; original untouched).</summary>
    public string? MasteredWavKey { get; set; }

    /// <summary>Mastered 320 kbps MP3 output (new key; original untouched).</summary>
    public string? MasteredMp3Key { get; set; }

    /// <summary>Engine-side reference (e.g. Tonn mastering task id) for finalize/poll.</summary>
    public string? EngineRef { get; set; }

    /// <summary>Preview storage key for the approval flow (preview engines only).</summary>
    public string? PreviewKey { get; set; }

    // ── Measured loudness ──
    public double? InputLufs { get; set; }
    public double? OutputLufs { get; set; }
    public double? OutputTruePeakDbtp { get; set; }

    /// <summary>Target integrated loudness (default −14 LUFS).</summary>
    public double TargetLufs { get; set; } = -14.0;

    /// <summary>Target true-peak ceiling (default −1.0 dBTP).</summary>
    public double TargetTruePeakDbtp { get; set; } = -1.0;

    /// <summary>Validation report JSON (metadata + artwork) shown to the user.</summary>
    public string? ValidationReportJson { get; set; }

    /// <summary>
    /// Credit ledger: set exactly once when a credit is consumed for this job.
    /// Null means no credit has been charged yet.
    /// </summary>
    public DateTime? ChargedAt { get; set; }

    /// <summary>
    /// Which credit pool funded this charge: <c>monthly</c> | <c>purchased</c>.
    /// Null when uncharged (or for legacy rows charged before purchased credits
    /// existed — treated as monthly). Monthly credits spend first.
    /// </summary>
    public string? CreditSource { get; set; }

    /// <summary>Worker retry counter — bounded to a single retry.</summary>
    public int RetryCount { get; set; }

    /// <summary>Current processing lease holder. Only this lease may heartbeat or finish the job.</summary>
    public Guid? ProcessingLeaseId { get; set; }

    /// <summary>UTC instant when the current processing lease expires.</summary>
    public DateTime? ProcessingLeaseExpiresAt { get; set; }

    /// <summary>UTC instant when the current processing attempt started.</summary>
    public DateTime? ProcessingStartedAt { get; set; }

    /// <summary>Last UTC heartbeat from the active worker lease holder.</summary>
    public DateTime? LastHeartbeatAt { get; set; }

    /// <summary>Top-level failure message (also captured to Sentry).</summary>
    public string? Error { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
