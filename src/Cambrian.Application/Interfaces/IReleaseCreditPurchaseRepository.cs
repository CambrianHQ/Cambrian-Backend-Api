using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

/// <summary>Data access for <see cref="ReleaseCreditPurchase"/> (purchased credit grants).</summary>
public interface IReleaseCreditPurchaseRepository
{
    /// <summary>Total paid credits ever granted to a creator (the purchased pool size).</summary>
    Task<int> SumPaidCreditsAsync(string creatorId, CancellationToken ct = default);

    /// <summary>True when a grant already exists for this Stripe session (webhook idempotency).</summary>
    Task<bool> ExistsForSessionAsync(string stripeSessionId, CancellationToken ct = default);

    Task AddAsync(ReleaseCreditPurchase purchase, CancellationToken ct = default);
}
