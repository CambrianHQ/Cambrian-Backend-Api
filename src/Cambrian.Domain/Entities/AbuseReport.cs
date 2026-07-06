namespace Cambrian.Domain.Entities;

public class AbuseReport
{
    public Guid Id { get; set; }

    /// <summary>Legacy track reference, populated when TargetType is "track". Nullable so
    /// deleting the reported track doesn't destroy the report's moderation history.</summary>
    public Guid? TrackId { get; set; }

    public Track? Track { get; set; }

    /// <summary>track, creator, user, comment, other.</summary>
    public string TargetType { get; set; } = "track";

    /// <summary>Generic id of the reported entity, as a string (works across target types).</summary>
    public string? TargetId { get; set; }

    public string Reason { get; set; } = "";

    public string? Details { get; set; }

    public string Status { get; set; } = "open"; // open, investigating, closed

    public DateTime ReportedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string? ReportedByUserId { get; set; }

    public DateTime? InvestigatedAt { get; set; }

    public string? InvestigatedByUserId { get; set; }

    public string? ResolutionNote { get; set; }
}
