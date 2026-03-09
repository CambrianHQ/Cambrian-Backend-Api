using Cambrian.Application.DTOs.Catalog;

namespace Cambrian.Application.Interfaces;

public interface ICreatorService
{
    Task<IReadOnlyCollection<TrackResponse>> GetTracksAsync(string userId, int page, int pageSize);

    Task<object> GetRevenueAsync(string userId);
}
