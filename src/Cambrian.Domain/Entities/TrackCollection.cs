namespace Cambrian.Domain.Entities;

public sealed class TrackCollection
{
    public Guid Id { get; set; }

    public string CreatorId { get; set; } = "";

    public string Title { get; set; } = "";

    /// <summary>URL-safe album slug, unique per creator (e.g. "midnight-sessions").</summary>
    public string Slug { get; set; } = "";

    public string? Description { get; set; }

    public string? CoverImageUrl { get; set; }

    /// <summary>
    /// Comma-separated track GUIDs in display order. Legacy column, dual-written
    /// with <see cref="AlbumTrack"/> rows so pre-albums consumers keep working.
    /// </summary>
    public string TrackIds { get; set; } = "";

    /// <summary>Album visibility: public | hidden. Hidden albums are owner-only.</summary>
    public string Visibility { get; set; } = "public";

    /// <summary>Optional public release date shown on the album page.</summary>
    public DateTime? ReleaseDate { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
