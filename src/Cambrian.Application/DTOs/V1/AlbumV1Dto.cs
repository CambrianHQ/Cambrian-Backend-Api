using Cambrian.Application.DTOs.Catalog;

namespace Cambrian.Application.DTOs.V1;

/// <summary>
/// Public/creator-facing album projection for the versioned API. Albums are
/// relationships over existing tracks (backed by <c>TrackCollection</c> +
/// <c>AlbumTrack</c> join rows), so this DTO carries only album metadata and
/// an ordered track-id list — never creator earnings, payment data, or PII.
/// </summary>
public class AlbumV1Dto
{
    public string Id { get; set; } = "";

    /// <summary>Owner's public creator identifier (opaque). No email/PII.</summary>
    public string CreatorId { get; set; } = "";

    public string Title { get; set; } = "";

    /// <summary>Stable, per-creator-unique URL slug. Never changes on rename.</summary>
    public string Slug { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>Album cover art URL (maps to the collection's cover image).</summary>
    public string? ArtworkUrl { get; set; }

    /// <summary>draft | public | unlisted | private.</summary>
    public string Visibility { get; set; } = "public";

    public DateTime? ReleaseDate { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>Number of tracks in the album (raw membership count).</summary>
    public int TrackCount { get; set; }

    /// <summary>Ordered track ids (album track order).</summary>
    public IReadOnlyList<string> TrackIds { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Album detail: album metadata plus a lightweight creator summary and the
/// hydrated, visibility-filtered public track projections in album order.
/// Tracks use <see cref="PublicCatalogTrackDto"/> (the anonymous-safe
/// allowlist), so this payload can never leak private track pricing/earnings.
/// </summary>
public sealed class AlbumDetailV1Dto : AlbumV1Dto
{
    public AlbumCreatorSummaryV1Dto Creator { get; set; } = new();

    public IReadOnlyList<PublicCatalogTrackDto> Tracks { get; set; } = Array.Empty<PublicCatalogTrackDto>();
}

/// <summary>
/// Minimal public identity for an album's owner: the same fields the storefront
/// already exposes for linking. No email, balance, or payout data.
/// </summary>
public sealed class AlbumCreatorSummaryV1Dto
{
    public string UserId { get; set; } = "";
    public string? CreatorId { get; set; }
    public string? Username { get; set; }
    public string? Slug { get; set; }
    public string? DisplayName { get; set; }
    public string? ProfileImageUrl { get; set; }
}
