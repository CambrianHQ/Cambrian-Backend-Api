namespace Cambrian.Application.Configuration;

/// <summary>A one-time Release Ready credit pack. Server-authoritative pricing.</summary>
public sealed record CreditPack(string Id, int Credits, int PriceCents);

/// <summary>
/// The credit packs offered for one-time top-ups. The price and credit count are
/// resolved on the server from the pack id ONLY — the client never dictates the
/// amount charged (money law). Must stay in sync with the frontend
/// <c>components/release-ready/creditPacks.ts</c>.
/// </summary>
public static class CreditPackCatalog
{
    public static readonly IReadOnlyList<CreditPack> Packs = new[]
    {
        new CreditPack("single", 1, 900),    // $9
        new CreditPack("triple", 3, 2400),   // $24
        new CreditPack("ten", 10, 6900),     // $69
    };

    /// <summary>Resolve a pack by id (case-insensitive); null when unknown.</summary>
    public static CreditPack? Find(string? id) =>
        id is null ? null : Packs.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Resolve the server-authoritative pack represented by a webhook grant count.</summary>
    public static CreditPack? FindByCredits(int credits) =>
        Packs.FirstOrDefault(p => p.Credits == credits);
}
