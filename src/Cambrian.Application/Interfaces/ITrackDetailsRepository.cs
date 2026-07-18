using Cambrian.Application.DTOs.Catalog;

namespace Cambrian.Application.Interfaces;

/// <summary>
/// Persistence for the optional track companion rows (lyrics, the "Behind The
/// Track" creation process, and its proof videos). Deliberately separate from
/// ITrackRepository: these writes must never touch the Track row itself.
/// </summary>
public interface ITrackDetailsRepository
{
    Task<TrackLyricsDto?> GetLyricsAsync(Guid trackId);

    Task<TrackLyricsDto> UpsertLyricsAsync(
        Guid trackId, string lyrics, string language, bool? isExplicit, int? expectedVersion = null);

    Task<bool> DeleteLyricsAsync(Guid trackId, int expectedVersion);

    Task<BehindTheTrackDto?> GetCreationProcessAsync(Guid trackId);

    Task<BehindTheTrackDto> UpsertCreationProcessAsync(
        Guid trackId,
        string? story,
        string? daw,
        string? vocalChain,
        string? promptNotes,
        string? productionNotes,
        string? humanContributionNotes,
        string? youtubeUrl,
        IReadOnlyList<string> toolsUsed);

    Task DeleteCreationProcessAsync(Guid trackId);

    /// <summary>Proof videos for a track, ordered by SortOrder. Pass includeHidden=true only for the owner.</summary>
    Task<IReadOnlyList<TrackVideoProofDto>> GetProofVideosAsync(Guid trackId, bool includeHidden);

    Task<TrackVideoProofDto?> GetProofVideoAsync(Guid trackId, Guid videoId);

    Task<TrackVideoProofDto> AddProofVideoAsync(
        Guid trackId, string videoType, string url, string? title, string? description, int sortOrder, string visibility);

    /// <summary>Partial update — pass null for any field that should be left unchanged. Returns null if not found.</summary>
    Task<TrackVideoProofDto?> UpdateProofVideoAsync(
        Guid trackId, Guid videoId, string? videoType, string? url, string? title, string? description, int? sortOrder, string? visibility);

    /// <summary>Returns true if a row was deleted.</summary>
    Task<bool> DeleteProofVideoAsync(Guid trackId, Guid videoId);

    /// <summary>Next SortOrder for a new proof video on this track (max existing + 1, or 0).</summary>
    Task<int> GetNextProofVideoSortOrderAsync(Guid trackId);
}
