namespace Cambrian.Domain.Entities;

public class Purchase
{
    public Guid Id { get; set; }

    public string BuyerId { get; set; } = "";

    public ApplicationUser Buyer { get; set; } = null!;

    public Guid TrackId { get; set; }

    public Track Track { get; set; } = null!;

    /// <summary>Amount in cents (e.g. 2999 = $29.99). Avoids floating-point rounding issues.</summary>
    public int AmountCents { get; set; }

    public string? PaymentMethod { get; set; }

    public string? LicenseType { get; set; }

    public string Status { get; set; } = "pending"; // pending, completed, refunded

    /// <summary>Intended usage context for the license (e.g. youtube, ads, podcast).</summary>
    public string UsageType { get; set; } = "personal";

    /// <summary>Stripe Checkout Session ID — used for webhook lookup.</summary>
    public string? StripeSessionId { get; set; }

    /// <summary>FK to the license certificate issued for this purchase (null until payment confirmed).</summary>
    public Guid? LicenseId { get; set; }

    public LicenseCertificate? License { get; set; }

    /// <summary>When the purchase was completed (payment confirmed).</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>When a pending purchase expires and should be ignored. Set to CreatedAt + 24 h.</summary>
    public DateTime? ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Last time the Status field was updated (for timeline tracking).</summary>
    public DateTime? UpdatedAt { get; set; }
}