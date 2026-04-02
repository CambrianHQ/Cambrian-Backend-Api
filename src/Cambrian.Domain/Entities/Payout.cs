namespace Cambrian.Domain.Entities;

public class Payout
{
    public Guid Id { get; set; }

    public string CreatorId { get; set; } = "";

    public ApplicationUser Creator { get; set; } = null!;

    /// <summary>Amount in cents (e.g. 2999 = $29.99). Avoids floating-point rounding issues.</summary>
    public int AmountCents { get; set; }

    public string Status { get; set; } = "pending"; // pending, approved, rejected, completed

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAt { get; set; }

    public string? FailureReason { get; set; }
}