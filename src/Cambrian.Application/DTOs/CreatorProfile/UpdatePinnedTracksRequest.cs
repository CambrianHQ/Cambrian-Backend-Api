namespace Cambrian.Application.DTOs.CreatorProfile;

public class UpdatePinnedTracksRequest
{
    /// <summary>Comma-separated track GUIDs in display order. Empty string to clear pins.</summary>
    public string? TrackIds { get; set; }
}
