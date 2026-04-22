using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.DTOs.Creator;

namespace Cambrian.Application.Interfaces;

public interface ICreatorService
{
    Task<PagedResult<TrackResponse>> GetTracksAsync(string userId, int page, int pageSize);

    Task<object> GetRevenueAsync(string userId);

    Task<CreatorDashboardResponse> GetDashboardAsync(string userId);
}
