namespace Cambrian.Domain.Entities;

public class AbuseReport
{
    public Guid Id { get; set; }

    public Guid TrackId { get; set; }

    public Track Track { get; set; } = null!;

    public string Reason { get; set; } = "";

    public string Status { get; set; } = "open"; // open, investigating, closed

    public DateTime ReportedAt { get; set; } = DateTime.UtcNow;

    public string? ReportedByUserId { get; set; }
}
