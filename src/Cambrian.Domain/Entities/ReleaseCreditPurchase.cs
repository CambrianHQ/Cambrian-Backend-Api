namespace Cambrian.Domain.Entities;

/// <summary>
/// A one-time purchase of Release Ready credits (a "pack"). Created by the Stripe
/// webhook when a credit-pack checkout completes. Purchased credits never expire;
/// the remaining purchased balance is DERIVED — SUM(<see cref="Credits"/> of paid
/// rows) minus the count of jobs charged against the purchased source — so there
/// is no mutable balance to drift, mirroring the monthly-credit design.
/// </summary>
public class ReleaseCreditPurchase
{
    public Guid Id { get; set; }

    /// <summary>Owner — FK to AspNetUsers.Id.</summary>
    public string CreatorId { get; set; } = "";

    /// <summary>Credits granted by this purchase (1, 3, or 10).</summary>
    public int Credits { get; set; }

    /// <summary>Amount paid, in cents (audit only — pricing is server-authoritative).</summary>
    public int AmountCents { get; set; }

    /// <summary>Pack identifier the purchase was made from (e.g. single | triple | ten).</summary>
    public string Pack { get; set; } = "";

    /// <summary>paid (granted). Reserved for future refund handling.</summary>
    public string Status { get; set; } = "paid";

    /// <summary>Stripe checkout session id — unique, the webhook idempotency key.</summary>
    public string? StripeSessionId { get; set; }

    /// <summary>Stripe PaymentIntent id used to reconcile refunds and disputes without a live API lookup.</summary>
    public string? StripePaymentIntentId { get; set; }

    public int RefundedAmountCents { get; set; }

    public DateTime? RefundedAt { get; set; }

    public DateTime? DisputedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
