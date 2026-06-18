namespace Cambrian.Domain.Entities;

public class Subscription
{
    public Guid Id { get; set; }

    public string UserId { get; set; } = "";

    public ApplicationUser User { get; set; } = null!;

    public string Plan { get; set; } = "free"; // free, paid, creator

    public string Status { get; set; } = "active"; // active, cancelled, expired

    public string? StripeSubscriptionId { get; set; }

    public string? StripeCustomerId { get; set; }

    /// <summary>
    /// The Stripe Checkout session that created this subscription. Backed by a
    /// unique filtered index so a duplicate/retried checkout.session.completed
    /// webhook cannot create a second subscription for the same session.
    /// </summary>
    public string? StripeSessionId { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ExpiresAt { get; set; }
}