namespace Cambrian.Domain.Entities;

/// <summary>
/// A paid, signed authorship attestation for a release (track). Created with
/// evidence at intake (<c>pending_payment</c>), issued by the Stripe webhook once
/// the purchase completes: the service hashes every evidence file into a SHA-256
/// manifest, freezes a canonical JSON record, and signs it with the platform
/// provenance key. The public verify view exposes no PII beyond the artist name.
/// </summary>
public class AuthorshipRecord
{
    public Guid Id { get; set; }

    /// <summary>The release this record attests — currently the catalog Track id.</summary>
    public Guid TrackId { get; set; }

    /// <summary>Owner — FK to AspNetUsers.Id. Never exposed publicly.</summary>
    public string CreatorId { get; set; } = "";

    /// <summary>Artist display name frozen at issue time — the only PII in the public view.</summary>
    public string ArtistName { get; set; } = "";

    /// <summary>pending_payment | issued | failed.</summary>
    public string Status { get; set; } = "pending_payment";

    /// <summary>
    /// Evidence as submitted at intake: file refs (storage keys), declarations,
    /// narrative, and generator/prompt metadata. Stored as JSON.
    /// </summary>
    public string EvidenceJson { get; set; } = "";

    /// <summary>
    /// SHA-256 manifest of all evidence files (canonical JSON array of
    /// <c>{key, sha256}</c>), computed at issue time. Null until issued.
    /// </summary>
    public string? ManifestJson { get; set; }

    /// <summary>SHA-256 hex of <see cref="CanonicalRecordJson"/> — the signed digest.</summary>
    public string? RecordHash { get; set; }

    /// <summary>The canonical (frozen) record JSON the signature covers. Null until issued.</summary>
    public string? CanonicalRecordJson { get; set; }

    /// <summary>Base64 signature over (<see cref="RecordHash"/>, <see cref="IssuedAt"/>). Null until issued.</summary>
    public string? Signature { get; set; }

    /// <summary>Signature algorithm identifier (e.g. ECDSA-P256-SHA256).</summary>
    public string? SignatureAlgorithm { get; set; }

    /// <summary>Identifier of the signing key (SHA-256 prefix of the SPKI).</summary>
    public string? KeyId { get; set; }

    /// <summary>Stripe checkout session that paid for this record (idempotency anchor).</summary>
    public string? StripeSessionId { get; set; }

    /// <summary>PaymentIntent used to reconcile refunds and disputes without a live Stripe lookup.</summary>
    public string? StripePaymentIntentId { get; set; }

    /// <summary>pending | paid | refunded | disputed.</summary>
    public string PaymentStatus { get; set; } = "pending";

    public DateTime? RefundedAt { get; set; }

    public DateTime? DisputedAt { get; set; }

    public DateTime? IssuedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
