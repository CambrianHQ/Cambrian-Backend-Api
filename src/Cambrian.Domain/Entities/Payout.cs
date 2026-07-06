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

    /// <summary>Stable key reused for every Stripe transfer attempt.</summary>
    public string? StripeIdempotencyKey { get; set; }

    /// <summary>Stripe transfer ID persisted once completion is confirmed.</summary>
    public string? StripeTransferId { get; set; }

    /// <summary>Admin user id who approved/rejected this payout, if reviewed.</summary>
    public string? ReviewedByUserId { get; set; }

    public DateTime? ReviewedAt { get; set; }

    /// <summary>Reason supplied by the admin when rejecting a pending payout.</summary>
    public string? RejectionReason { get; set; }
}
