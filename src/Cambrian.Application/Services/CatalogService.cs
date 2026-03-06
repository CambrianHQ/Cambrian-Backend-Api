using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;

namespace Cambrian.Application.Services;

public class CatalogService : ICatalogService
{
    private readonly ITrackRepository _tracks;

    public CatalogService(ITrackRepository tracks)
    {
        _tracks = tracks;
    }

    public async Task<IReadOnlyCollection<TrackResponse>> GetCatalogAsync(int page = 1, int pageSize = 50, string? genre = null, string? search = null, string? sort = null)
    {
        var tracks = await _tracks.BrowseAsync(page, pageSize, genre, search, sort);

        return tracks.Select(t => MapToResponse(t)).ToList();
    }

    public async Task<IReadOnlyCollection<TrackResponse>> GetDiscoverAsync(int page = 1, int pageSize = 20, string? genre = null, string? search = null)
    {
        return await GetCatalogAsync(page, pageSize, genre, search);
    }

    public async Task<TrackResponse?> GetTrackAsync(string trackId)
    {
        if (!Guid.TryParse(trackId, out var id))
            return null;

        var track = await _tracks.GetByIdAsync(id);

        return track is null ? null : MapToResponse(track);
    }

    private static TrackResponse MapToResponse(Track t) => new()
    {
        Id = t.Id.ToString(),
        Title = t.Title,
        Genre = t.Genre ?? "",
        Price = (decimal)t.Price
    };
}