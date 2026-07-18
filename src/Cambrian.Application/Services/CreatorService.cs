using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.DTOs.Creator;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Cambrian.Application.Services;

public class CreatorService : ICreatorService
{
    private readonly ITrackRepository _tracks;
    private readonly IPurchaseRepository _purchases;
    private readonly IPayoutRepository _payouts;
    private readonly IWalletRepository _wallet;
    private readonly IStreamRepository _streams;
    private readonly UserManager<ApplicationUser> _users;
    private readonly ICreatorIdentityRepository _creators;
    private readonly ICreatorProfileRepository _profiles;
    private readonly ICreatorMilestoneRepository _milestones;
    private readonly ILogger<CreatorService> _logger;

    public CreatorService(
        ITrackRepository tracks,
        IPurchaseRepository purchases,
        IPayoutRepository payouts,
        IWalletRepository wallet,
        IStreamRepository streams,
        UserManager<ApplicationUser> users,
        ICreatorIdentityRepository creators,
        ICreatorProfileRepository profiles,
        ICreatorMilestoneRepository milestones,
        ILogger<CreatorService> logger)
    {
        _tracks = tracks;
        _purchases = purchases;
        _payouts = payouts;
        _wallet = wallet;
        _streams = streams;
        _users = users;
        _creators = creators;
        _profiles = profiles;
        _milestones = milestones;
        _logger = logger;
    }

    public async Task<PagedResult<TrackResponse>> GetTracksAsync(string userId, int page, int pageSize)
    {
        var creatorUuid = await _creators.GetCreatorIdForUserAsync(userId);
        var tracks = await _tracks.GetCreatorTrackSummariesAsync(userId, creatorUuid);
        _logger.LogInformation("Creator dashboard: User={UserId} tracks={Count}", userId, tracks.Count);

        var creator = await _users.FindByIdAsync(userId);
        var profile = await _profiles.GetByUserIdAsync(userId);

        var items = tracks
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t =>
            {
                // Fallback: if NonExclusivePriceCents is 0, use legacy Price field (matches checkout logic)
                var legacyPriceDollars = t.Price;
                var nonExPrice = t.NonExclusivePriceCents > 0 ? t.NonExclusivePriceCents / 100m : legacyPriceDollars;

                return new TrackResponse
                {
                    Id = t.Id.ToString(),
                    CambrianTrackId = t.CambrianTrackId,
                    Title = t.Title,
                    Description = t.Description,
                    Genre = t.Genre ?? "",
                    PrimaryGenre = null,
                    Subgenre = t.Genre,
                    Mood = t.Mood,
                    Tempo = t.Tempo,
                    Tags = t.Tags,
                    Instrumental = t.Instrumental,
                    Visibility = t.Visibility,
                    Price = nonExPrice,
                    NonExclusivePrice = nonExPrice,
                    AudioUrl = t.AudioUrl ?? "",
                    CoverArtUrl = t.CoverArtUrl,
                    Status = t.Status == "exclusive_sold" || t.Status == "copyright_transferred" ? "available" : (t.Status ?? "available"),
                    Duration = t.Duration,
                    CreatorId = userId,
                    CreatorSlug = profile?.Slug,
                    CreatorProfileImageUrl = profile?.ProfileImageUrl,
                    Artist = creator?.DisplayName ?? creator?.UserName ?? "Unknown Artist",
                    CreatedAt = t.CreatedAt,
                };
            })
            .ToList();

        return new PagedResult<TrackResponse>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = tracks.Count,
        };
    }

    public async Task<object> GetRevenueAsync(string userId)
    {
        var creatorUuid = await _creators.GetCreatorIdForUserAsync(userId);
        var tracks = await _tracks.GetDashboardTrackSummariesAsync(userId, creatorUuid);
        var trackIds = tracks.Select(t => t.Id).ToHashSet();

        var allPurchases = new List<Domain.Entities.Purchase>();
        foreach (var trackId in trackIds)
        {
            var tp = await _purchases.GetByTrackIdAsync(trackId);
            allPurchases.AddRange(tp.Where(p => p.Status == "completed"));
        }

        // Resolve creator's actual fee rate from TierManifest
        var creator = await _users.FindByIdAsync(userId);
        var feeRate = creator is not null
            ? TierManifest.For(creator.CreatorTier).FeeRate
            : TierManifest.Free.FeeRate;

        var grossCents = allPurchases.Sum(p => p.AmountCents);
        var totalGross = grossCents / 100m;
        // Use per-purchase floor to match wallet credit calculation,
        // consistent with PayoutService.GetEarningsAsync
        var totalEarnedCents = allPurchases.Sum(p => (long)Math.Floor(p.AmountCents * (1 - feeRate)));
        var totalEarned = totalEarnedCents / 100m;
        var totalPlatformFee = Math.Round(totalGross - totalEarned, 2);

        var payouts = await _payouts.GetByCreatorIdAsync(userId);
        var pendingPayouts = payouts.Where(p => p.Status == "pending").Sum(p => p.AmountCents) / 100m;
        var paidOut = payouts.Where(p => p.Status == "completed").Sum(p => p.AmountCents) / 100m;

        return new
        {
            totalEarned,
            totalGross,
            totalPlatformFee,
            platformFeePercent = feeRate,
            pendingBalance = totalEarned - paidOut - pendingPayouts,
            pendingPayouts,
            paidOut
        };
    }

    public async Task<CreatorDashboardResponse> GetDashboardAsync(string userId)
    {
        // Total earnings = SUM(AmountCents) FROM WalletTransactions WHERE Type='credit'
        var totalEarnings = await _wallet.GetTotalCreditsAsync(userId);

        // Weekly earnings = same query with CreatedAt >= 7 days ago
        var weeklyEarnings = await _wallet.GetCreditsAfterAsync(userId, DateTime.UtcNow.AddDays(-7));

        // Load creator tracks for per-track breakdown
        var creatorUuid = await _creators.GetCreatorIdForUserAsync(userId);
        var tracks = await _tracks.GetDashboardTrackSummariesAsync(userId, creatorUuid);
        var trackIds = tracks.Select(t => t.Id).ToList();

        // Plays = qualified lifetime counts from the TrackStats projection.
        var playCounts = trackIds.Count > 0
            ? await _streams.GetPlayCountsByTrackIdsAsync(trackIds)
            : new Dictionary<Guid, long>();

        // Sales = COUNT(*) FROM Purchases WHERE Status='completed' AND TrackId IN (creator tracks)
        var saleCounts = trackIds.Count > 0
            ? await _purchases.GetCompletedCountsByTrackIdsAsync(trackIds)
            : new Dictionary<Guid, int>();

        // Earned per track = SUM(AmountCents) FROM WalletTransactions joined to Purchases
        var earnedByTrack = trackIds.Count > 0
            ? await _wallet.GetCreditsByTrackAsync(userId, trackIds)
            : new Dictionary<Guid, long>();

        var totalPlays = playCounts.Values.Sum();
        var totalSales = saleCounts.Values.Sum();

        // Conversion = sales / plays * 100
        var conversion = totalPlays > 0
            ? Math.Round((decimal)totalSales / totalPlays * 100, 2)
            : 0m;

        var trackStats = tracks.Select(t => new TrackStatsDto
        {
            Id = t.Id.ToString(),
            Title = t.Title,
            CoverArtUrl = t.CoverArtUrl,
            Sales = saleCounts.GetValueOrDefault(t.Id),
            Plays = playCounts.GetValueOrDefault(t.Id),
            EarnedCents = earnedByTrack.GetValueOrDefault(t.Id),
        }).ToList();

        // Lifecycle milestones — earliest-ever timestamps, so re-reads are
        // stable (idempotent) and the frontend emitter can dedupe safely.
        var firstPlay = await _milestones.GetFirstPlayAsync(userId);
        var firstFan = await _milestones.GetFirstFanEventAsync(userId);

        return new CreatorDashboardResponse
        {
            EarningsCents = totalEarnings,
            WeeklyEarningsCents = weeklyEarnings,
            TotalPlays = totalPlays,
            TotalSales = totalSales,
            ConversionRate = conversion,
            Tracks = trackStats,
            Milestones = new CreatorMilestonesDto
            {
                FirstPlay = firstPlay is null ? null : new MilestoneFirstPlayDto
                {
                    At = firstPlay.AtUtc,
                    TrackId = firstPlay.TrackId.ToString(),
                },
                FirstFan = firstFan is null ? null : new MilestoneFirstFanDto
                {
                    At = firstFan.AtUtc,
                    Source = firstFan.Source,
                },
            },
        };
    }
}
