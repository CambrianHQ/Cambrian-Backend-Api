namespace Cambrian.Application.DTOs.ReleaseReady;

/// <summary>Credit balance for the signed-in creator.</summary>
public sealed class CreditStatusDto
{
    public int Allowance { get; init; }
    public int Used { get; init; }
    public int Remaining { get; init; }
    public string Plan { get; init; } = "free";
}

/// <summary>Result of the upload+validate step: a draft job and its validation report.</summary>
public sealed class ValidateResponse
{
    public Guid JobId { get; init; }
    public string Engine { get; init; } = "ffmpeg";
    public bool RequiresApproval { get; init; }
    public ValidationReport Validation { get; init; } = new();
}

/// <summary>Full job state for the status/polling endpoint.</summary>
public sealed class JobDto
{
    public Guid Id { get; init; }
    public Guid? TrackId { get; init; }
    public string Engine { get; init; } = "ffmpeg";

    /// <summary>validated | queued | processing | awaiting_approval | done | failed.</summary>
    public string Status { get; init; } = "validated";

    public bool RequiresApproval { get; init; }
    public ValidationReport? Validation { get; init; }
    public double? InputLufs { get; init; }
    public double? OutputLufs { get; init; }
    public double? OutputTruePeakDbtp { get; init; }
    public string? PreviewUrl { get; init; }
    public bool WavReady { get; init; }
    public bool Mp3Ready { get; init; }
    public string? Error { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
}

public sealed class JobSummaryDto
{
    public Guid Id { get; init; }
    public string Status { get; init; } = "validated";
    public string Engine { get; init; } = "ffmpeg";
    public DateTime CreatedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
}

/// <summary>Input to the upload+validate step (controller maps multipart form to this).</summary>
public sealed class ReleaseReadyUploadInput
{
    public required string UserId { get; init; }
    public required Stream Audio { get; init; }
    public string AudioFileName { get; init; } = "audio";
    public Stream? Artwork { get; init; }
    public string? ArtworkFileName { get; init; }
    public Guid? TrackId { get; init; }

    /// <summary>DDEX AI-disclosure: creator-declared AI involvement.</summary>
    public bool AiGenerated { get; init; }

    /// <summary>DDEX AI-disclosure: structured/free-text disclosure stored on the track.</summary>
    public string? AiDisclosure { get; init; }

    /// <summary>Optional target loudness override (defaults to −14 LUFS).</summary>
    public double? TargetLufs { get; init; }
}

/// <summary>A mastered-asset download: either a signed URL (S3) or a streamed body (local).</summary>
public sealed class MasteringDownload
{
    public string? SignedUrl { get; init; }
    public Stream? Content { get; init; }
    public string ContentType { get; init; } = "application/octet-stream";
    public string FileName { get; init; } = "master";
}
