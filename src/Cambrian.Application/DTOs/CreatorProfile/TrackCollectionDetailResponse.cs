using Cambrian.Application.DTOs.Catalog;

namespace Cambrian.Application.DTOs.CreatorProfile;

public class TrackCollectionDetailResponse
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? CoverImageUrl { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public CollectionCreatorSummary Creator { get; set; } = new();
    public IReadOnlyList<TrackResponse> Tracks { get; set; } = Array.Empty<TrackResponse>();
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
