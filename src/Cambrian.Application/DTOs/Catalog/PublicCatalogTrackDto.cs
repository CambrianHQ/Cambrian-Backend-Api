namespace Cambrian.Application.DTOs.Catalog;

/// <summary>
/// Explicit, anonymous-safe projection of a track for the public catalog/discovery
/// routes (<c>/catalog</c>, <c>/discover</c>, <c>/tracks</c>, <c>/track/{id}</c>,
/// <c>/tracks/{id}</c>, <c>/catalog/{id}</c>, <c>/trending</c>). Contains ONLY what an
/// unauthenticated listener needs.
///
/// F18: the public catalog previously serialized <see cref="TrackResponse"/> directly,
/// exposing platform fee and per-track creator earnings to any anonymous visitor.
/// This projection is an allowlist — it carries no financial or internal fields
/// (no fee, earnings, status, or visibility). Do not add financial fields here, and
/// never serialize <see cref="TrackResponse"/> (or any entity) on an anonymous route
/// again; the public-route financial-leak test enforces this.
///
/// (Named to avoid a clash with the SEO-surface <c>DTOs.Public.PublicTrackDto</c>,
/// which has a different shape.)
/// </summary>
public sealed class PublicCatalogTrackDto
{
    public string Id { get; init; } = string.Empty;

    /// <summary>Cambrian track identifier (CAMB-TRK-XXXX).</summary>
    public string CambrianTrackId { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    /// <summary>Alias for Title — backward compatibility with clients expecting 'name'.</summary>
    public string Name => Title;

    public string? Description { get; init; }

    public string Genre { get; init; } = string.Empty;

    public string? PrimaryGenre { get; init; }

    public string? Subgenre { get; init; }

    public string? Mood { get; init; }

    public string? Tempo { get; init; }

    public ICollection<string> Tags { get; init; } = new List<string>();

    public bool Instrumental { get; init; }

    // Public list prices. A marketplace must show the sale price to anonymous
    // buyers, so `price`/`nonExclusivePrice` stay — these are buyer-facing list
    // prices, NOT the platform fee or creator take-home (those are deliberately
    // absent). The leak test blocks (earning|fee|revenue|payout|stripe) but allows `price`.
    public decimal Price { get; init; }

    public decimal NonExclusivePrice { get; init; }

    public string? Duration { get; init; }

    public string? AudioUrl { get; init; }

    /// <summary>Whether this track has a streamable audio source.</summary>
    public bool StreamAvailable { get; init; }

    public string? CoverArtUrl { get; init; }

    /// <summary>Sanitized public availability status (e.g. "available"). Retired
    /// licensing statuses are masked to "available" upstream — safe to expose.</summary>
    public string Status { get; init; } = "available";

    /// <summary>Public creator identifier used for storefront links and image paths.</summary>
    public string CreatorId { get; init; } = string.Empty;

    /// <summary>Creator's public storefront slug (for linking to /creator/{slug}).</summary>
    public string? CreatorSlug { get; init; }

    public string? CreatorProfileImageUrl { get; init; }

    public string? Artist { get; init; }

    /// <summary>Lifetime public play count (preview + full plays, incl. anonymous).</summary>
    public int Plays { get; init; }

    /// <summary>Number of completed (paid) purchases for this track.</summary>
    public int Sales { get; init; }

    /// <summary>Id of the issued Human Authorship Record for this track, if any; null otherwise.</summary>
    public string? AuthorshipRecordId { get; init; }

    public bool AiGenerated { get; init; }

    /// <summary>Provenance state: "none" | "hashed" | "stamped" | "verified". Never exposes the raw hash/signature.</summary>
    public string ProvenanceStatus { get; init; } = "none";

    public DateTime CreatedAt { get; init; }

    public static PublicCatalogTrackDto From(TrackResponse t) => new()
    {
        Id = t.Id,
        CambrianTrackId = t.CambrianTrackId,
        Title = t.Title,
        Description = t.Description,
        Genre = t.Genre,
        PrimaryGenre = t.PrimaryGenre,
        Subgenre = t.Subgenre,
        Mood = t.Mood,
        Tempo = t.Tempo,
        Tags = t.Tags,
        Instrumental = t.Instrumental,
        Price = t.Price,
        NonExclusivePrice = t.NonExclusivePrice,
        Duration = t.Duration,
        AudioUrl = t.AudioUrl,
        StreamAvailable = t.StreamAvailable,
        CoverArtUrl = t.CoverArtUrl,
        Status = t.Status,
        CreatorId = t.CreatorId,
        CreatorSlug = t.CreatorSlug,
        CreatorProfileImageUrl = t.CreatorProfileImageUrl,
        Artist = t.Artist,
        Plays = t.Plays,
        Sales = t.Sales,
        AuthorshipRecordId = t.AuthorshipRecordId,
        AiGenerated = t.AiGenerated,
        ProvenanceStatus = t.ProvenanceStatus,
        CreatedAt = t.CreatedAt,
    };
}
