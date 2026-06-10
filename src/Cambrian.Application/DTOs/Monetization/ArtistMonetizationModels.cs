namespace Cambrian.Application.DTOs.Monetization;

/// <summary>Body for POST /api/artists/{id}/tip.</summary>
public sealed class TipRequest
{
    /// <summary>Tip amount in cents. Bounds enforced server-side ($1–$1,000).</summary>
    public int AmountCents { get; set; }
}

/// <summary>Response for tip + subscribe — the Stripe-hosted checkout URL.</summary>
public sealed class MonetizationCheckoutResponse
{
    public string CheckoutUrl { get; init; } = "";
}

/// <summary>Body for PUT /api/artists/me/subscription-price.</summary>
public sealed class SetSubscriptionPriceRequest
{
    /// <summary>Monthly price in cents ($1–$500); null disables fan subscriptions.</summary>
    public int? PriceCents { get; set; }
}
