namespace Cambrian.Application.DTOs.Sync;

public class CreateSyncBriefRequest
{
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public string? Genre { get; set; }
    public string? Mood { get; set; }
    public decimal Budget { get; set; }
    public DateTime Deadline { get; set; }
    public string? UsageType { get; set; }
    public string? Territory { get; set; }
}

public class SyncBriefDto
{
    public Guid Id { get; set; }
    public string BuyerUserId { get; set; } = null!;
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public string? Genre { get; set; }
    public string? Mood { get; set; }
    public decimal Budget { get; set; }
    public DateTime Deadline { get; set; }
    public string? UsageType { get; set; }
    public string? Territory { get; set; }
    public string Status { get; set; } = null!;
    public int SubmissionCount { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class SyncBriefDetailDto : SyncBriefDto
{
    public List<SyncSubmissionDto> Submissions { get; set; } = new();
}

public class SubmitToSyncBriefRequest
{
    public Guid TrackId { get; set; }
    public string? Note { get; set; }
}

public class SyncSubmissionDto
{
    public Guid Id { get; set; }
    public string CreatorUserId { get; set; } = null!;
    public Guid TrackId { get; set; }
    public string? Note { get; set; }
    public DateTime SubmittedAt { get; set; }
    public string Status { get; set; } = null!;
}
