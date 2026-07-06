using Cambrian.Application.DTOs.Catalog;

namespace Cambrian.Application.Interfaces;

/// <summary>
/// Persistence for the optional 1:1 track companion rows (lyrics and the
/// "Behind The Track" creation process). Deliberately separate from
/// ITrackRepository: these writes must never touch the Track row itself.
/// </summary>
public interface ITrackDetailsRepository
{
    Task<TrackLyricsDto?> GetLyricsAsync(Guid trackId);

    Task<TrackLyricsDto> UpsertLyricsAsync(Guid trackId, string lyrics, string language);

    Task DeleteLyricsAsync(Guid trackId);

    Task<BehindTheTrackDto?> GetCreationProcessAsync(Guid trackId);

    Task<BehindTheTrackDto> UpsertCreationProcessAsync(Guid trackId, string? story, string? youtubeUrl, IReadOnlyList<string> toolsUsed);

    Task DeleteCreationProcessAsync(Guid trackId);
}
