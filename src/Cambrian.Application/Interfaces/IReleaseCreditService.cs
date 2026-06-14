using Cambrian.Application.DTOs.ReleaseReady;

namespace Cambrian.Application.Interfaces;

/// <summary>
/// Release Ready monthly credits. Allowance is a per-tier config constant
/// (<c>TierConfig.ReleaseReadyCreditsPerMonth</c>); usage is derived from
/// <see cref="Cambrian.Domain.Entities.MasteringJob.ChargedAt"/> within the
/// current calendar month — so there is no balance to drift and no rollover.
/// </summary>
public interface IReleaseCreditService
{
    Task<CreditStatusDto> GetStatusAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Atomically consume one credit for the job, in a single transaction so two
    /// concurrent submits cannot both pass with one credit left. Idempotent per
    /// job (an already-charged job returns true without double-charging). Returns
    /// <c>false</c> when no credits remain — the caller blocks submit/approve.
    /// Jobs in <c>failed</c> status are excluded from the monthly count, so a
    /// terminal failure releases the credit (the audit row remains).
    /// </summary>
    Task<bool> TryChargeAsync(Guid jobId, string userId, CancellationToken ct = default);

    /// <summary>
    /// Start a one-time credit-pack purchase. Resolves the price from the pack id
    /// server-side (never the client), creates a Stripe checkout session, and returns
    /// the redirect URL. Purchased credits are granted by the webhook on completion.
    /// Throws <see cref="InvalidOperationException"/> for an unknown pack or missing config.
    /// </summary>
    Task<CreditCheckoutResponse> CreateCreditCheckoutAsync(string userId, string packId, CancellationToken ct = default);
}
