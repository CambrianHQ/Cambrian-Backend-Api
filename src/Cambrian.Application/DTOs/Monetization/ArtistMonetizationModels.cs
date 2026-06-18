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

/// <summary>
/// Creator-facing summary of money-in (tips + fan subscriptions) for GET /api/artists/me/support.
/// Always scoped to the calling artist. Net figures are post-platform-fee. Payer identity is
/// intentionally omitted (only aggregate totals and recent amounts/timestamps are exposed).
/// </summary>
public sealed class CreatorSupportSummaryResponse
{
    public long TotalNetCents { get; init; }
    public long TipNetCents { get; init; }
    public long SubscriptionNetCents { get; init; }
    public int TipCount { get; init; }
    public int SubscriptionCount { get; init; }
    public int ActiveFanSubscribers { get; init; }
    public IReadOnlyList<CreatorSupportEventDto> Recent { get; init; } = System.Array.Empty<CreatorSupportEventDto>();
}

/// <summary>A single money-in event in the creator's support feed.</summary>
public sealed class CreatorSupportEventDto
{
    /// <summary>"tip" | "sub" | "commission".</summary>
    public string Source { get; init; } = "";
    public long GrossCents { get; init; }
    public long NetCents { get; init; }
    public string Currency { get; init; } = "usd";
    public System.DateTime CreatedAt { get; init; }
}
