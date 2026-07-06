namespace Cambrian.Application.DTOs.CreatorProfile;

public class TrackCollectionDto
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>URL-safe album slug, unique per creator.</summary>
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public string? CoverImageUrl { get; set; }
    public IReadOnlyList<string> TrackIds { get; set; } = Array.Empty<string>();
    /// <summary>Album visibility: public | hidden.</summary>
    public string Visibility { get; set; } = "public";
    public DateTime? ReleaseDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
