namespace Cambrian.Application.DTOs.Public;

/// <summary>
/// Public, SEO/AI-safe representation of a single track. Built only from data that is
/// safe to crawl: title, copy, taxonomy, public price, public engagement counts, a
/// public audio-preview URL, and provenance status. Excludes raw storage keys, the
/// platform-fee / creator-earnings breakdown, owner/copyright internals, and the raw
/// content hash / signature.
/// </summary>
public sealed class PublicTrackDto : PublicSeoResource
{
    /// <summary>Stable UUID for the track.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable Cambrian track ID (CAMB-TRK-XXXX) — preferred public identifier.</summary>
    public string TrackId { get; set; } = string.Empty;

    public string? Genre { get; set; }
    public string? PrimaryGenre { get; set; }
    public string? Subgenre { get; set; }
    public string? Mood { get; set; }
    public string? Tempo { get; set; }
    public bool Instrumental { get; set; }

    /// <summary>Display duration string (e.g. "3:24"), when known.</summary>
    public string? Duration { get; set; }

    /// <summary>Listed price in whole currency units (USD).</summary>
    public decimal Price { get; set; }

    /// <summary>Listed price in cents — the canonical integer money value.</summary>
    public int PriceCents { get; set; }

    public string Currency { get; set; } = "USD";

    /// <summary>Whether a public audio preview is available to stream.</summary>
    public bool StreamAvailable { get; set; }

    /// <summary>Absolute, public audio-preview stream URL (proxied; never a raw storage key).</summary>
    public string? AudioPreviewUrl { get; set; }

    /// <summary>Lightweight reference to the track's creator.</summary>
    public PublicCreatorRef? Creator { get; set; }

    /// <summary>Lifetime qualified-play count.</summary>
    public long Plays { get; set; }

    /// <summary>Completed (paid) sales — real, sourced from completed purchases.</summary>
    public int Sales { get; set; }

    /// <summary>Whether the creator disclosed this track as AI-generated/assisted (DDEX disclosure).</summary>
    public bool AiGenerated { get; set; }

    /// <summary>
    /// Real provenance state derived from the §9 pipeline. One of:
    /// "none" (no hash yet), "hashed" (content hash recorded), "stamped"
    /// (free ECDSA provenance stamp issued), "verified" (stamped + commercial-rights
    /// attestation). Never exposes the raw hash or signature.
    /// </summary>
    public string ProvenanceStatus { get; set; } = "none";

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Minimal public reference to a creator, embedded inside track DTOs so a consumer can
/// link to the creator without a second request. No private data.
/// </summary>
public sealed class PublicCreatorRef
{
    public string? Slug { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string CanonicalUrl { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
}
