namespace Cambrian.Domain.Entities;

public sealed class TrackCollection
{
    public Guid Id { get; set; }

    public string CreatorId { get; set; } = "";

    public string Title { get; set; } = "";

    public string? Description { get; set; }

    public string? CoverImageUrl { get; set; }

    /// <summary>Comma-separated track GUIDs in display order.</summary>
    public string TrackIds { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
