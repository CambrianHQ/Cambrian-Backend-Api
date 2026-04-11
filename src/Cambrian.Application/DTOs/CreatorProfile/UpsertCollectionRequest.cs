namespace Cambrian.Application.DTOs.CreatorProfile;

public class UpsertCollectionRequest
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? CoverImageUrl { get; set; }
    public string? TrackIds { get; set; }
}
