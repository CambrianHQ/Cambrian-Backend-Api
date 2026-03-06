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

    public async Task<IReadOnlyCollection<TrackResponse>> GetCatalogAsync()
    {
        var tracks = await _tracks.BrowseAsync();

        return tracks.Select(t => MapToResponse(t)).ToList();
    }

    public async Task<IReadOnlyCollection<TrackResponse>> GetDiscoverAsync()
    {
        return await GetCatalogAsync();
    }

    public async Task<TrackResponse?> GetTrackAsync(string trackId)
    {
        if (!Guid.TryParse(trackId, out var id))
            return null;

        var track = await _tracks.GetByIdAsync(id);

        return track is null ? null : MapToResponse(track);
    }

    public async Task<TrackResponse> UploadTrackAsync(UploadTrackRequest request)
    {
        var track = new Track
        {
            Id = Guid.NewGuid(),
            Title = request.Title,
            Genre = request.Genre,
            Price = (double)request.Price,
            CreatorId = "" // Will be set from authenticated user in controller
        };

        await _tracks.AddAsync(track);

        return MapToResponse(track);
    }

    private static TrackResponse MapToResponse(Track t) => new()
    {
        Id = t.Id.ToString(),
        Title = t.Title,
        Genre = t.Genre ?? "",
        Price = (decimal)t.Price
    };
}