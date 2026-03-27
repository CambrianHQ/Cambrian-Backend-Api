using Cambrian.Application.DTOs.Catalog;

namespace Cambrian.Application.Interfaces;

public interface IActivityService
{
    Task<IReadOnlyList<ActivityItemResponse>> GetNewAsync(CancellationToken ct);
    Task<IReadOnlyList<ActivityItemResponse>> GetSalesAsync(CancellationToken ct);
    Task<IReadOnlyList<TrendingTrackResponse>> GetTrendingAsync(CancellationToken ct);
}
