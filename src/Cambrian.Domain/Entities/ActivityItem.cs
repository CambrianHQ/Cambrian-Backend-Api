namespace Cambrian.Domain.Entities;

public sealed class ActivityItem
{
    public Guid Id { get; set; }

    /// <summary>Activity type: "sale" or "new".</summary>
    public string Type { get; set; } = null!;

    public Guid? TrackId { get; set; }

    public string? UserId { get; set; }

    /// <summary>Source entity ID (Purchase.Id for sales, Track.Id for new uploads) for idempotent backfill.</summary>
    public Guid? SourceId { get; set; }

    public bool IsSimulated { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
