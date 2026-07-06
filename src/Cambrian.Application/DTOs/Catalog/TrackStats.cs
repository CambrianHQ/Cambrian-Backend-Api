namespace Cambrian.Application.DTOs.Catalog;

/// <summary>
/// Live engagement counts for a single track, aggregated from the StreamSessions
/// and Purchases tables. Computed on read; never stored on the Track row.
/// </summary>
public sealed class TrackStats
{
    /// <summary>Number of recorded stream sessions (plays) for the track.</summary>
    public int Plays { get; set; }

    /// <summary>Number of completed (paid) purchases for the track.</summary>
    public int Sales { get; set; }

    /// <summary>
    /// Id of this track's ISSUED Human Authorship Record, if one exists; null
    /// otherwise. Drives the public "Verified authorship" surfaces.
    /// </summary>
    public string? AuthorshipRecordId { get; set; }
}
