namespace Cambrian.Application.DTOs.Authorship;

/// <summary>Evidence intake for POST /api/releases/{id}/authorship-record.</summary>
public sealed class CreateAuthorshipRecordRequest
{
    /// <summary>Storage keys of previously-uploaded evidence files (stems, project files, drafts).</summary>
    public List<EvidenceFileRef> Evidence { get; set; } = new();

    /// <summary>Creator declarations (e.g. "I wrote all lyrics", "melody composed by me").</summary>
    public List<string> Declarations { get; set; } = new();

    /// <summary>Free-text creation narrative.</summary>
    public string? Narrative { get; set; }

    /// <summary>Generator/prompt metadata when AI tooling was used.</summary>
    public GeneratorMetadata? Generator { get; set; }
}

public sealed class EvidenceFileRef
{
    /// <summary>Storage key of the uploaded evidence file.</summary>
    public string FileKey { get; set; } = "";

    public string? Description { get; set; }
}

public sealed class GeneratorMetadata
{
    public string? Tool { get; set; }
    public string? Version { get; set; }
    public List<string> Prompts { get; set; } = new();
}

/// <summary>Result of evidence intake — payment completes the record.</summary>
public sealed class CreateAuthorshipRecordResponse
{
    public Guid RecordId { get; init; }
    public string CheckoutUrl { get; init; } = "";
}

/// <summary>Owner view (GET /api/authorship-records/{id}).</summary>
public sealed class AuthorshipRecordResponse
{
    public Guid Id { get; init; }
    public Guid TrackId { get; init; }

    /// <summary>pending_payment | issued | failed.</summary>
    public string Status { get; init; } = "";

    public DateTime CreatedAt { get; init; }
    public DateTime? IssuedAt { get; init; }

    /// <summary>The signed certificate — present once issued.</summary>
    public AuthorshipCertificate? Certificate { get; init; }
}

/// <summary>
/// The verifiable certificate. Also the public verify view — contains no PII
/// beyond the artist name.
/// </summary>
public sealed class AuthorshipCertificate
{
    public Guid RecordId { get; init; }
    public string ArtistName { get; init; } = "";

    /// <summary>The exact canonical JSON the signature covers.</summary>
    public string CanonicalRecord { get; init; } = "";

    /// <summary>SHA-256 hex of <see cref="CanonicalRecord"/>.</summary>
    public string RecordHash { get; init; } = "";

    /// <summary>Base64 signature over (record hash, issued-at).</summary>
    public string Signature { get; init; } = "";

    public string Algorithm { get; init; } = "";
    public string KeyId { get; init; } = "";
    public string PublicKeyPem { get; init; } = "";
    public DateTime IssuedAt { get; init; }

    /// <summary>Human/machine instructions for independent verification.</summary>
    public string VerificationInstructions { get; init; } = "";
}

/// <summary>Internal data needed to render the owner-only PDF certificate.</summary>
public sealed class AuthorshipCertificateDocument
{
    public Guid RecordId { get; init; }
    public Guid TrackId { get; init; }
    public string TrackTitle { get; init; } = "";
    public string TrackCode { get; init; } = "";
    public string CreatorName { get; init; } = "";
    public string RecordHash { get; init; } = "";
    public string Signature { get; init; } = "";
    public string Algorithm { get; init; } = "";
    public string KeyId { get; init; } = "";
    public string? ChainAnchor { get; init; }
    public string AuthorshipSummary { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public DateTime IssuedAt { get; init; }
    public string VerificationDisplayUrl { get; init; } = "";
    public string VerificationQrUrl { get; init; } = "";

    /// <summary>Stable storage version. Changes when the signed record changes.</summary>
    public string Version => string.IsNullOrWhiteSpace(RecordHash) ? "pending" : RecordHash;
}

/// <summary>Public response for GET /api/verify/{hash}. No auth and no PII.</summary>
public sealed class AuthorshipHashVerificationResponse
{
    public bool Found { get; init; }
    public string TrackTitle { get; init; } = "";
    public string CreatorName { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public string? ChainAnchor { get; init; }
    public string RecordUrl { get; init; } = "";
}
