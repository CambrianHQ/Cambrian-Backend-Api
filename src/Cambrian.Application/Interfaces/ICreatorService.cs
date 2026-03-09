using Cambrian.Application.DTOs.Catalog;

namespace Cambrian.Application.Interfaces;

public interface ICreatorService
{
    Task<List<TrackResponse>> GetTracksAsync(string creatorId, int page, int pageSize);

    Task<CreatorRevenueResponse> GetRevenueAsync(string creatorId);
}

public class CreatorRevenueResponse
{
    public decimal TotalEarned { get; set; }
    public decimal PendingBalance { get; set; }
    public decimal PendingPayouts { get; set; }
    public decimal PaidOut { get; set; }
}
