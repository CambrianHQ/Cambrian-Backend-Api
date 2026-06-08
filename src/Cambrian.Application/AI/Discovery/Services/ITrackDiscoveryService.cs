using Cambrian.Application.AI.Discovery.Dtos;
using Cambrian.Application.AI.Discovery.Queries;

namespace Cambrian.Application.AI.Discovery.Services;

public interface ITrackDiscoveryService
{
    Task<AiTrackSearchResponseDto> SearchAsync(SearchTracksQuery query);

    Task<AiTrackDetailsDto?> GetTrackDetailsAsync(string trackId);

    Task<AiTrackPreviewDto?> GetPreviewAsync(string trackId);

    Task<AiCreatorProfileDto?> GetCreatorProfileAsync(string creatorId);
}
