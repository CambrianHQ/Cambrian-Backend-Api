namespace Cambrian.Application.DTOs.Catalog;

public sealed class TrendingTrackResponse
{
    public Guid TrackId { get; set; }
    public string Title { get; set; } = "";
    public decimal Score { get; set; }
    public string? UseCase { get; set; }
}
