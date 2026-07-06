using Cambrian.Application.AI.Discovery.Dtos;
using Cambrian.Application.AI.Discovery.Queries;

namespace Cambrian.Application.AI.Discovery.Services;

public interface ITrackDiscoveryService
{
    Task<AiTrackSearchResponse> SearchAsync(SearchTracksQuery query);

    Task<AiTrackDetails?> GetTrackDetailsAsync(string trackId);

    Task<AiTrackPreview?> GetPreviewAsync(string trackId);

    Task<AiCreatorProfile?> GetCreatorProfileAsync(string creatorId);

    Task<List<AiLicenseOption>?> GetLicenseOptionsAsync(string trackId);
}
