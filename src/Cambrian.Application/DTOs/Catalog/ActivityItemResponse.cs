namespace Cambrian.Application.DTOs.Catalog;

public sealed class ActivityItemResponse
{
    public string Type { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public Guid? TrackId { get; set; }
    public string? TrackTitle { get; set; }
}
