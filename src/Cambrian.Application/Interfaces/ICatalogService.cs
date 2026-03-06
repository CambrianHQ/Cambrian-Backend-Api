using Cambrian.Application.DTOs.Catalog;

namespace Cambrian.Application.Interfaces;

public interface ICatalogService
{
    Task<IReadOnlyCollection<TrackResponse>> GetCatalogAsync();

    Task<IReadOnlyCollection<TrackResponse>> GetDiscoverAsync();

    Task<TrackResponse?> GetTrackAsync(string trackId);

    Task<TrackResponse> UploadTrackAsync(UploadTrackRequest request);
}