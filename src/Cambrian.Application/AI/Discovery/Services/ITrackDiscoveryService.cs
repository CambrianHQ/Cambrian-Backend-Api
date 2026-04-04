using Cambrian.Application.AI.Discovery.Dtos;
using Cambrian.Application.AI.Discovery.Queries;
using Cambrian.Application.DTOs.Catalog;

namespace Cambrian.Application.AI.Discovery.Services;

public interface ITrackDiscoveryService
{
    Task<PagedResult<TrackSearchResultDto>> SearchAsync(SearchTracksQuery query);
    Task<TrackDetailsDto?> GetDetailsAsync(GetTrackDetailsQuery query);
    Task<List<LicenseOptionDto>> GetLicenseOptionsAsync(GetLicenseOptionsQuery query);
    Task<CreatorProfileDetailDto?> GetCreatorProfileAsync(GetCreatorProfileQuery query);
}
