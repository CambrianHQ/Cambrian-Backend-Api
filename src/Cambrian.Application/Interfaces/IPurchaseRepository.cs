using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

public interface IPurchaseRepository
{
    Task<Purchase?> GetByIdAsync(Guid id);

    Task<List<Purchase>> GetByBuyerIdAsync(string buyerId);

    Task<List<Purchase>> GetByTrackIdAsync(Guid trackId);

    /// <summary>Returns all purchases for tracks owned by this creator.</summary>
    Task<List<Purchase>> GetByCreatorIdAsync(string creatorId, Guid? creatorUuid = null);

    /// <summary>Look up a purchase by its Stripe Checkout Session ID.</summary>
    Task<Purchase?> GetByStripeSessionIdAsync(string stripeSessionId);

    Task<Dictionary<Guid, int>> GetCompletedCountsByTrackIdsAsync(IEnumerable<Guid> trackIds);

    Task AddAsync(Purchase purchase);

    Task UpdateAsync(Purchase purchase);

    /// <summary>
    /// Returns true when the user has at least one completed purchase for this track.
    /// Used by download endpoints — library membership alone does not grant download access.
    /// </summary>
    Task<bool> HasCompletedPurchaseAsync(string userId, Guid trackId);
}
