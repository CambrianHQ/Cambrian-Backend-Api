namespace Cambrian.Domain.Entities;

/// <summary>
/// Append-only artist earnings ledger row. One row per money-in event
/// (tip, fan-subscription payment, or marketplace commission credit).
/// Rows are never updated or deleted; corrections are new rows.
///
/// <para>Write-side is owned by the money-in webhooks; read/aggregation
/// endpoints are owned elsewhere. Idempotency: a unique index on
/// (<see cref="Source"/>, <see cref="ExternalRef"/>) makes webhook retries no-ops.</para>
/// </summary>
public class EarningsTransaction
{
    public Guid Id { get; set; }

    /// <summary>Artist receiving the earnings — FK to AspNetUsers.Id.</summary>
    public string ArtistUserId { get; set; } = "";

    /// <summary>tip | sub | commission.</summary>
    public string Source { get; set; } = "";

    /// <summary>Gross amount paid by the fan, in cents.</summary>
    public long GrossCents { get; set; }

    /// <summary>Platform fee withheld, in cents (0 for tips at launch; 15% for subs).</summary>
    public long FeeCents { get; set; }

    /// <summary>Net amount owed to the artist: gross − fee.</summary>
    public long NetCents { get; set; }

    /// <summary>ISO currency code (lowercase), e.g. usd.</summary>
    public string Currency { get; set; } = "usd";

    /// <summary>
    /// External reference that uniquely identifies the money event — the Stripe
    /// checkout session id for tips/first sub payment, invoice id for renewals,
    /// purchase id for commissions. Combined with <see cref="Source"/> it is unique.
    /// </summary>
    public string ExternalRef { get; set; } = "";

    /// <summary>Paying user when known (null for anonymous tips).</summary>
    public string? PayerUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
