namespace Cambrian.Domain.Entities;

public class Track
{
    public Guid Id { get; set; }

    /// <summary>Human-readable Cambrian track ID (e.g. CAMB-TRK-A1B2C3D4).</summary>
    public string CambrianTrackId { get; set; } = "";

    public string Title { get; set; } = "";

    public string? Description { get; set; }

    public string? Genre { get; set; }

    /// <summary>Top-level genre taxonomy label (e.g. Hip-Hop, Electronic, Cinematic).</summary>
    public string? PrimaryGenre { get; set; }

    /// <summary>Leaf genre taxonomy label used for the canonical genre alias.</summary>
    public string? Subgenre { get; set; }

    /// <summary>Mood tag for search filtering (e.g. happy, dark, chill, energetic).</summary>
    public string? Mood { get; set; }

    /// <summary>Tempo description or BPM value for search filtering.</summary>
    public string? Tempo { get; set; }

    /// <summary>Whether the track is instrumental (no vocals).</summary>
    public bool Instrumental { get; set; }

    public decimal Price { get; set; }

    public string? Duration { get; set; }

    public string? LicenseType { get; set; }

    public string? AudioUrl { get; set; }

    public string? CoverArtUrl { get; set; }

    public int NonExclusivePriceCents { get; set; }

    public int ExclusivePriceCents { get; set; }

    public int CopyrightBuyoutPriceCents { get; set; }

    public bool ExclusiveSold { get; set; }

    /// <summary>Track availability status: available, exclusive_sold, copyright_transferred.</summary>
    public string Status { get; set; } = "available";

    /// <summary>User ID of the current copyright owner (defaults to creator; changes on copyright buyout).</summary>
    public string? CopyrightOwnerId { get; set; }

    /// <summary>Timestamp when copyright was transferred via buyout.</summary>
    public DateTime? CopyrightTransferredAt { get; set; }

    /// <summary>Original creator ID preserved after copyright transfer.</summary>
    public string? OriginalCreatorId { get; set; }

    public string Visibility { get; set; } = "public"; // public, limited, hidden

    /// <summary>
    /// SHA-256 hex digest of the stored audio bytes, computed on upload (§9 provenance).
    /// Nullable: existing rows are backfilled by a one-off pass, so a null here means
    /// "not yet hashed" rather than "no audio".
    /// </summary>
    public string? ContentHash { get; set; }

    /// <summary>
    /// Server-signed provenance stamp over (<see cref="ContentHash"/>, <see cref="SignedAt"/>):
    /// base64 ECDSA P-256 / SHA-256 signature. Free, instant, independently verifiable with the
    /// platform public key — issued the moment the track is hashed. Null until hashed/signed.
    /// </summary>
    public string? Signature { get; set; }

    /// <summary>UTC time the provenance stamp was signed (truncated to whole seconds). Null until signed.</summary>
    public DateTime? SignedAt { get; set; }

    /// <summary>
    /// Whether the creator's commercial rights to this track have been verified.
    /// Batch-1 placeholder: currently a creator self-attestation set via the authorship
    /// upsert (or by an admin). The real verification flow (document upload + review,
    /// "Verified Clean" badge) is §9 item 5 and will replace the write path.
    /// </summary>
    public bool CommercialRightsVerified { get; set; }

    /// <summary>
    /// DDEX AI-disclosure: whether the track is AI-generated/assisted. Captured at
    /// Release Ready validation and surfaced in the DDEX export. Defaults false.
    /// </summary>
    public bool AiGenerated { get; set; }

    /// <summary>
    /// DDEX AI-disclosure: structured payload (tools/models used, per-element roles)
    /// as JSON, aligned to DDEX AI-disclosure fields. Null until disclosed.
    /// </summary>
    public string? AiDisclosureDdex { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string CreatorId { get; set; } = "";

    /// <summary>UUID FK to Creators table. The canonical creator relationship.</summary>
    public Guid? CreatorUuid { get; set; }

    public ApplicationUser Creator { get; set; } = null!;

    /// <summary>Navigation to the first-class Creator entity via CreatorUuid.</summary>
    public Creator? CreatorEntity { get; set; }

    public ICollection<string> Tags { get; set; } = new List<string>();

    public ICollection<Purchase> Purchases { get; set; } = new List<Purchase>();

    public ICollection<LibraryItem> LibraryItems { get; set; } = new List<LibraryItem>();

    /// <summary>Optional use-case tag for discovery (e.g. "vlog", "podcast", "gaming").</summary>
    public string? UseCase { get; set; }

    /// <summary>Computed trending score; default 0; never required for existing tracks.</summary>
    public decimal TrendingScore { get; set; }

    /// <summary>Editorial "featured" placement set by an admin. One-way (no unfeature action yet).</summary>
    public bool IsFeatured { get; set; }

    public DateTime? FeaturedAt { get; set; }

    public string? FeaturedByUserId { get; set; }

    /// <summary>Editorial "pinned" placement set by an admin. One-way (no unpin action yet).</summary>
    public bool IsPinned { get; set; }

    public DateTime? PinnedAt { get; set; }

    public string? PinnedByUserId { get; set; }
}
