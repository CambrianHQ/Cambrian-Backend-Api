namespace Cambrian.Application.DTOs.Catalog;

public sealed class AnalyticsEventRequest
{
    public string? Type { get; set; }
    public Guid? TrackId { get; set; }
    public string? MetadataJson { get; set; }
}
