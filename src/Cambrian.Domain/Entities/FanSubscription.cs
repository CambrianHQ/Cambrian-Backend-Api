namespace Cambrian.Domain.Entities;

/// <summary>
/// A fan's recurring subscription to an artist, billed on the artist's Stripe
/// connected account at the artist-set price with a 15% platform application fee.
/// Lifecycle is driven by Connect webhooks (checkout completed → active;
/// customer.subscription.deleted → cancelled).
/// </summary>
public class FanSubscription
{
    public Guid Id { get; set; }

    /// <summary>Subscribing fan — FK to AspNetUsers.Id.</summary>
    public string FanUserId { get; set; } = "";

    /// <summary>Artist being supported — FK to AspNetUsers.Id.</summary>
    public string ArtistUserId { get; set; } = "";

    /// <summary>Monthly price in cents, frozen at subscribe time.</summary>
    public int PriceCents { get; set; }

    /// <summary>pending | active | past_due | cancelled | checkout_failed.</summary>
    public string Status { get; set; } = "pending";

    /// <summary>Stripe subscription id on the artist's connected account.</summary>
    public string? StripeSubscriptionId { get; set; }

    /// <summary>Stripe checkout session that started this subscription.</summary>
    public string? StripeSessionId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ActivatedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public DateTime? PaymentFailedAt { get; set; }
}
