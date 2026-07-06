using Cambrian.Application.DTOs.Catalog;

namespace Cambrian.Application.DTOs.CreatorProfile;

/// <summary>
/// Public album detail: album metadata + hydrated public track projections +
/// creator summary. Tracks use <see cref="PublicCatalogTrackDto"/> (the
/// deliberate public allowlist) so this endpoint can never leak private
/// pricing/earnings fields.
/// </summary>
public class TrackCollectionDetailResponse
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public string? CoverImageUrl { get; set; }
    public string Visibility { get; set; } = "public";
    public DateTime? ReleaseDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public CollectionCreatorSummary Creator { get; set; } = new();
    public IReadOnlyList<PublicCatalogTrackDto> Tracks { get; set; } = Array.Empty<PublicCatalogTrackDto>();
    public IReadOnlyList<string> TrackIds { get; set; } = Array.Empty<string>();
}

public class CollectionCreatorSummary
{
    public string UserId { get; set; } = "";
    public string? CreatorId { get; set; }
    public string? Username { get; set; }
    public string? Slug { get; set; }
    public string? DisplayName { get; set; }
    public string? ProfileImageUrl { get; set; }
}
