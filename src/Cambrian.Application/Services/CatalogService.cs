using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;

namespace Cambrian.Application.Services;

public class CatalogService : ICatalogService
{
    public Task<IReadOnlyCollection<TrackResponse>> GetCatalogAsync()
    {
        IReadOnlyCollection<TrackResponse> items =
        [
            new TrackResponse
            {
                Id = Guid.NewGuid().ToString(),
                Title = "Starter Track",
                Genre = "ambient",
                Price = 19.99m
            }
        ];

        return Task.FromResult(items);
    }

    public Task<IReadOnlyCollection<TrackResponse>> GetDiscoverAsync()
    {
        return GetCatalogAsync();
    }

    public Task<TrackResponse?> GetTrackAsync(string trackId)
    {
        TrackResponse response = new()
        {
            Id = trackId,
            Title = "Starter Track",
            Genre = "ambient",
            Price = 19.99m
        };

        return Task.FromResult<TrackResponse?>(response);
    }

    public Task<TrackResponse> UploadTrackAsync(UploadTrackRequest request)
    {
        var response = new TrackResponse
        {
            Id = Guid.NewGuid().ToString(),
            Title = request.Title,
            Genre = request.Genre,
            Price = request.Price
        };

        return Task.FromResult(response);
    }
}
