using Cambrian.Application.DTOs.Monetization;
using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

/// <summary>
/// Connect money-in: tips and fan subscriptions billed on the artist's connected
/// account (direct charges). Tips carry no platform fee at launch; fan
/// subscriptions carry a 15% application fee. Both throw
/// <see cref="Cambrian.Application.Exceptions.ArtistPayoutsNotEnabledException"/>
/// (→ 409) when the artist's Connect account can't receive payouts.
/// </summary>
public interface IArtistMonetizationService
{
    /// <summary>Platform application fee on fan subscriptions (percent).</summary>
    const decimal FanSubscriptionFeePercent = 15m;

    Task<MonetizationCheckoutResponse> CreateTipCheckoutAsync(
        string artistIdentifier, int amountCents, string payerUserId, CancellationToken ct = default);

    Task<MonetizationCheckoutResponse> CreateFanSubscriptionCheckoutAsync(
        string artistIdentifier, string payerUserId, CancellationToken ct = default);

    /// <summary>Artist-side configuration of the monthly fan-subscription price.</summary>
    Task SetSubscriptionPriceAsync(string artistUserId, int? priceCents, CancellationToken ct = default);
}

/// <summary>Data access for <see cref="FanSubscription"/> (repository-pattern governance).</summary>
public interface IFanSubscriptionRepository
{
    Task<FanSubscription?> GetAsync(Guid id, CancellationToken ct = default);
    Task<FanSubscription?> GetByStripeSubscriptionIdAsync(string stripeSubscriptionId, CancellationToken ct = default);

    /// <summary>An existing pending/active subscription from this fan to this artist (idempotent subscribe).</summary>
    Task<FanSubscription?> GetLiveByFanAndArtistAsync(string fanUserId, string artistUserId, CancellationToken ct = default);

    Task AddAsync(FanSubscription subscription, CancellationToken ct = default);
    Task UpdateAsync(FanSubscription subscription, CancellationToken ct = default);
}
