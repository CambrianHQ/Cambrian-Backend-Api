using Cambrian.Application.DTOs.Catalog;

namespace Cambrian.Application.DTOs.CreatorProfile;

public class StorefrontResponse
{
    public CreatorProfileDto Profile { get; set; } = new();

    public CreatorStatsDto Stats { get; set; } = new();

    public IReadOnlyList<TrackResponse> PinnedTracks { get; set; } = [];

    public IReadOnlyList<TrackCollectionDto> Collections { get; set; } = [];

    public IReadOnlyList<TrackResponse> Tracks { get; set; } = [];
}
