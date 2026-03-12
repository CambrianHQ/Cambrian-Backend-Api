using Cambrian.Application.DTOs.Catalog;

namespace Cambrian.Application.Interfaces;

public interface ICatalogService
{
    Task<IReadOnlyCollection<TrackResponse>> GetCatalogAsync(int page = 1, int pageSize = 50, string? genre = null, string? search = null, string? sort = null);

    Task<IReadOnlyCollection<TrackResponse>> GetCatalogAsync(int page, int pageSize, string? genre, string? search, string? sort,
        string? mood, string? tempo, bool? instrumental, string? duration);

    Task<IReadOnlyCollection<TrackResponse>> GetDiscoverAsync(int page = 1, int pageSize = 20, string? genre = null, string? search = null);

    Task<IReadOnlyCollection<TrackResponse>> GetDiscoverAsync(int page, int pageSize, string? genre, string? search,
        string? mood, string? tempo, bool? instrumental, string? duration);

    Task<TrackResponse?> GetTrackAsync(string trackId);
}