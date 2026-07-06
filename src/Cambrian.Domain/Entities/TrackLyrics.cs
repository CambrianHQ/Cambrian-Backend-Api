namespace Cambrian.Domain.Entities;

/// <summary>
/// Creator-authored lyrics for a track (1:1). Kept in its own table so lyric
/// edits never touch the Track row — engagement data and URLs are untouched
/// by lyric changes. Publicly readable when the track is visible; rendered
/// server-side for SEO indexing.
/// </summary>
public sealed class TrackLyrics
{
    /// <summary>PK and FK to Tracks.Id (one lyrics row per track).</summary>
    public Guid TrackId { get; set; }

    public string Lyrics { get; set; } = "";

    /// <summary>BCP-47 language tag (e.g. "en", "es", "pt-BR").</summary>
    public string Language { get; set; } = "en";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
