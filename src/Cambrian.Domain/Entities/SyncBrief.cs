namespace Cambrian.Domain.Entities;

public class SyncBrief
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

    /// <summary>open, reviewing, closed, filled</summary>
    public string Status { get; set; } = "open";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ApplicationUser Buyer { get; set; } = null!;

    public ICollection<SyncSubmission> Submissions { get; set; } = new List<SyncSubmission>();
}
