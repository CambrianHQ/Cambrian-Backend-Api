namespace Cambrian.Application.DTOs.Admin;

public class AdminAbuseReport
{
    public string Id { get; set; } = string.Empty;

    /// <summary>track, creator, user, comment, other.</summary>
    public string TargetType { get; set; } = "track";

    public string? TargetId { get; set; }

    /// <summary>Convenience fields populated when TargetType is "track".</summary>
    public string? TrackId { get; set; }

    public string? TrackTitle { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string? Details { get; set; }

    public string Status { get; set; } = "open"; // open, investigating, closed

    public string? ReportedByUserId { get; set; }

    public DateTime ReportedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? InvestigatedAt { get; set; }

    public string? InvestigatedByUserId { get; set; }

    public string? ResolutionNote { get; set; }
}
