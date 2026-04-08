namespace Cambrian.Domain.Constants;

/// <summary>
/// Canonical string values for <see cref="Cambrian.Domain.Entities.Purchase.Status"/>.
///
/// Background: the audit found ~25 sites duplicating these literals; a typo in any one
/// of them produces a silent zero-row result. This class is the only place these
/// strings are spelled out — every Purchase.Status read or write should reference
/// these constants so the compiler can catch typos.
///
/// The values match the on-disk strings exactly so no migration or value converter
/// is needed.
/// </summary>
public static class PurchaseStatuses
{
    public const string Pending = "pending";
    public const string Completed = "completed";
    public const string Refunded = "refunded";
    public const string Disputed = "disputed";
}
