namespace Cambrian.Application.DTOs.CreatorProfile;

public class TrackCollectionDto
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? CoverImageUrl { get; set; }
    public string TrackIds { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
