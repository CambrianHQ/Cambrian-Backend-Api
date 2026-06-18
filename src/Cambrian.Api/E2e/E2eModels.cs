namespace Cambrian.Api.E2e;

/// <summary>A deterministic seeded account, returned in the seed manifest.</summary>
public sealed record E2eAccount(
    string Role,
    string Email,
    string Password,
    string UserId,
    string CreatorTier,
    string? CreatorHandle);

/// <summary>A deterministic seeded track, returned in the seed manifest.</summary>
public sealed record E2eSeedTrack(
    string Kind,
    string TrackId,
    string CambrianTrackId,
    string Visibility,
    bool AudioAvailable,
    bool HasAuthorship);

/// <summary>
/// The full result of a reset/seed. Every field is deterministic across runs (fixed ids,
/// emails, handles and a shared known password), so two seeds produce an identical manifest.
/// </summary>
public sealed record E2eManifest(
    bool Seeded,
    string Password,
    string CreatorProfileHandle,
    IReadOnlyList<E2eAccount> Accounts,
    IReadOnlyList<E2eSeedTrack> Tracks);

/// <summary>Outcome of a simulated Stripe webhook (used by the payment-control endpoints).</summary>
public sealed record E2eWebhookResult(
    string EventId,
    string Type,
    bool Processed,
    bool Deduplicated);
