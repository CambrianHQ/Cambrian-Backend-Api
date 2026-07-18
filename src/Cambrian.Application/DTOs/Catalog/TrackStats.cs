namespace Cambrian.Application.DTOs.Catalog;

/// <summary>
/// Live engagement counts for a single track. Qualified plays come from the
/// transactionally maintained TrackStats projection; sales come from completed purchases.
/// </summary>
public sealed class TrackStats
{
    /// <summary>Number of qualified plays for the track.</summary>
    public long Plays { get; set; }

    /// <summary>Number of completed (paid) purchases for the track.</summary>
    public int Sales { get; set; }

    /// <summary>
    /// Id of this track's ISSUED Human Authorship Record, if one exists; null
    /// otherwise. Drives the public "Verified authorship" surfaces.
    /// </summary>
    public string? AuthorshipRecordId { get; set; }
}
