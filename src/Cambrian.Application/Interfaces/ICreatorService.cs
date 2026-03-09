using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.DTOs.Creator;

namespace Cambrian.Application.Interfaces;

public interface ICreatorService
{
    Task<IReadOnlyCollection<TrackResponse>> GetTracksAsync(string userId, int page = 1, int pageSize = 50);

    Task<CreatorRevenueResponse> GetRevenueAsync(string userId);
}
