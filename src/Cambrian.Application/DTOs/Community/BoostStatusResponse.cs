namespace Cambrian.Application.DTOs.Community;

/// <summary>
/// Boost state for a single track, returned by the boost/un-boost/status
/// endpoints so the UI can render the button without a second round-trip.
/// </summary>
public sealed class BoostStatusResponse
{
    public string TrackId { get; set; } = "";

    /// <summary>Total number of boosts the track currently has.</summary>
    public int BoostCount { get; set; }

    /// <summary>Whether the current (authenticated) user has boosted this track.</summary>
    public bool HasBoosted { get; set; }
}
