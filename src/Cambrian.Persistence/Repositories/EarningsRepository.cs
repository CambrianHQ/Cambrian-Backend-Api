using Cambrian.Application.DTOs.Monetization;
using Cambrian.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Persistence.Repositories;

/// <summary>
/// Read access to the append-only <c>EarningsTransaction</c> ledger for the creator dashboard.
/// Every query is filtered by <c>ArtistUserId</c> so a creator can only ever see their own
/// money-in (no cross-creator leakage). Payer identity is never projected into the response.
/// </summary>
public sealed class EarningsRepository : IEarningsRepository
{
    private readonly CambrianDbContext _db;

    public EarningsRepository(CambrianDbContext db) => _db = db;

    public async Task<CreatorSupportSummaryResponse> GetSummaryForArtistAsync(
        string artistUserId, int recentTake = 20, CancellationToken ct = default)
    {
        if (recentTake is < 1 or > 100) recentTake = 20;

        var bySource = await _db.EarningsTransactions
            .Where(e => e.ArtistUserId == artistUserId)
            .GroupBy(e => e.Source)
            .Select(g => new { Source = g.Key, Count = g.Count(), Net = g.Sum(x => x.NetCents) })
            .ToListAsync(ct);

        long NetOf(string source) => bySource.FirstOrDefault(x => x.Source == source)?.Net ?? 0;
        int CountOf(string source) => bySource.FirstOrDefault(x => x.Source == source)?.Count ?? 0;

        var recent = await _db.EarningsTransactions
            .Where(e => e.ArtistUserId == artistUserId)
            .OrderByDescending(e => e.CreatedAt)
            .Take(recentTake)
            .Select(e => new CreatorSupportEventDto
            {
                Source = e.Source,
                GrossCents = e.GrossCents,
                NetCents = e.NetCents,
                Currency = e.Currency,
                CreatedAt = e.CreatedAt,
            })
            .ToListAsync(ct);

        var activeFanSubscribers = await _db.FanSubscriptions
            .CountAsync(s => s.ArtistUserId == artistUserId && s.Status == "active", ct);

        return new CreatorSupportSummaryResponse
        {
            TotalNetCents = bySource.Sum(x => x.Net),
            TipNetCents = NetOf("tip"),
            SubscriptionNetCents = NetOf("sub"),
            TipCount = CountOf("tip"),
            SubscriptionCount = CountOf("sub"),
            ActiveFanSubscribers = activeFanSubscribers,
            Recent = recent,
        };
    }
}
