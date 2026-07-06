namespace Cambrian.Domain.Entities;

/// <summary>
/// One "proof video" attached to a track's Behind The Track section — evidence
/// of the creation process (screen recordings, DAW walkthroughs, etc). Many
/// rows per track, ordered by <see cref="SortOrder"/>.
/// </summary>
public sealed class TrackVideoProof
{
    public Guid Id { get; set; }

    /// <summary>FK to Tracks.Id.</summary>
    public Guid TrackId { get; set; }

    /// <summary>"YouTube" or "External".</summary>
    public string VideoType { get; set; } = "YouTube";

    public string Url { get; set; } = "";

    public string? Title { get; set; }

    public string? Description { get; set; }

    public int SortOrder { get; set; }

    /// <summary>"public" or "hidden" — independent of the parent track's own visibility.</summary>
    public string Visibility { get; set; } = "public";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Track Track { get; set; } = null!;
}
