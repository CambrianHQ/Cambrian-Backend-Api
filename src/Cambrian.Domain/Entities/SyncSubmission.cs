namespace Cambrian.Domain.Entities;

public class SyncSubmission
{
    public Guid Id { get; set; }

    public Guid SyncBriefId { get; set; }

    public string CreatorUserId { get; set; } = null!;

    public Guid TrackId { get; set; }

    public string? Note { get; set; }

    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;

    /// <summary>pending, selected, rejected</summary>
    public string Status { get; set; } = "pending";

    public SyncBrief SyncBrief { get; set; } = null!;

    public ApplicationUser Creator { get; set; } = null!;

    public Track Track { get; set; } = null!;
}
