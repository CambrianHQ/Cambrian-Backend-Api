namespace Cambrian.Domain.Entities;

/// <summary>
/// Join row linking a track to an album (TrackCollection) with an explicit
/// position. Albums are relationships only — tracks are never moved, copied,
/// or recreated, so plays/boosts/purchases and URLs survive any album edit.
/// The legacy <see cref="TrackCollection.TrackIds"/> CSV is dual-written for
/// backward compatibility; these rows are the canonical ordering source.
/// </summary>
public sealed class AlbumTrack
{
    /// <summary>FK to TrackCollections.Id (albums are TrackCollections).</summary>
    public Guid AlbumId { get; set; }

    public Guid TrackId { get; set; }

    /// <summary>Zero-based display order within the album.</summary>
    public int Position { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
