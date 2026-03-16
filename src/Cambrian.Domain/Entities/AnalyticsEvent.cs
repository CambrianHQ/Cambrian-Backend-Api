namespace Cambrian.Domain.Entities;

public class AnalyticsEvent
{
    public Guid Id { get; set; }

    /// <summary>
    /// Event type: play, download, purchase, search, upload
    /// </summary>
    public string EventType { get; set; } = "";

    public string? UserId { get; set; }

    public Guid? TrackId { get; set; }

    /// <summary>
    /// Optional metadata — search query text, license type, etc.
    /// </summary>
    public string? Metadata { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
