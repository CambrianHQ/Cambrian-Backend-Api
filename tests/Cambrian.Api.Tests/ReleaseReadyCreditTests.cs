using System.Text;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.ReleaseReady;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Domain.Enums;
using Cambrian.Persistence;
using Cambrian.Application.Services;
using Cambrian.Infrastructure.Mastering;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cambrian.Api.Tests;

/// <summary>
/// Release Ready credit accounting against the real EF stack (relational fixture:
/// PostgreSQL via Testcontainers, SQLite fallback). Exercises <see cref="IReleaseCreditService"/>
/// + <see cref="IMasteringJobRepository"/> + <c>EfTransactionManager</c> end-to-end so the
/// "charged once per job, derived monthly usage, failed-jobs-release-the-credit, atomic"
/// rules from the frozen contract are verified against actual database counting — not a mock.
/// </summary>
[Trait("Category", "ReleaseReady")]
public sealed class ReleaseReadyCreditTests : IClassFixture<RelationalCambrianApiFixture>
{
    private readonly RelationalCambrianApiFixture _fixture;

    public ReleaseReadyCreditTests(RelationalCambrianApiFixture fixture) => _fixture = fixture;

    private const int CreatorAllowance = 3;  // TierManifest.Creator.ReleaseReadyCreditsPerMonth
    private const int ProAllowance = 10;      // TierManifest.Pro.ReleaseReadyCreditsPerMonth

    // ── 1. A credit is consumed exactly once per job (idempotent charge) ──

    [Fact]
    public async Task TryCharge_TwiceForSameJob_ConsumesExactlyOneCredit()
    {
        // Sanity-check the allowance constant the test depends on.
        TierManifest.For(CreatorTier.Creator).ReleaseReadyCreditsPerMonth.Should().Be(CreatorAllowance);

        var (userId, _) = await SeedCreatorAsync();
        var jobId = await SeedJobAsync(userId, status: "validated");

        await using var scope = _fixture.Services.CreateAsyncScope();
        var credits = scope.ServiceProvider.GetRequiredService<IReleaseCreditService>();

        var before = await credits.GetStatusAsync(userId);
        before.Allowance.Should().Be(CreatorAllowance);
        before.Used.Should().Be(0);
        before.Remaining.Should().Be(CreatorAllowance);

        // Charge the SAME job twice. The second call is idempotent — it must not
        // spend a second credit (ChargedAt is the ledger, set exactly once).
        var first = await credits.TryChargeAsync(jobId, userId);
        var second = await credits.TryChargeAsync(jobId, userId);

        first.Should().BeTrue();
        second.Should().BeTrue("an already-charged job is idempotent, not a second charge");

        var after = await credits.GetStatusAsync(userId);
        after.Used.Should().Be(1, "the single job consumed exactly one credit despite two charge calls");
        after.Remaining.Should().Be(CreatorAllowance - 1, "remaining drops by exactly 1");

        // The persisted ledger confirms exactly one charged row for this creator.
        await AssertChargedNonFailedCountAsync(userId, 1);
    }

    // ── 2. Zero remaining credits blocks submit (403 / InsufficientCreditsException) ──

    [Fact]
    public async Task ZeroRemaining_TryCharge_ReturnsFalse_AndSubmitIsBlocked()
    {
        var (userId, _) = await SeedCreatorAsync();

        // Exhaust the full monthly allowance with already-charged jobs this month.
        for (var i = 0; i < CreatorAllowance; i++)
            await SeedJobAsync(userId, status: "queued", chargedAt: DateTime.UtcNow);

        var fresh = await SeedJobAsync(userId, status: "validated");

        await using var scope = _fixture.Services.CreateAsyncScope();
        var credits = scope.ServiceProvider.GetRequiredService<IReleaseCreditService>();

        var status = await credits.GetStatusAsync(userId);
        status.Used.Should().Be(CreatorAllowance);
        status.Remaining.Should().Be(0, "the creator is at their monthly allowance");

        // At allowance: a further charge must be denied (caller turns false → 403).
        var charged = await credits.TryChargeAsync(fresh, userId);
        charged.Should().BeFalse("no credits remain, so the charge is refused");

        // No new credit was spent — the fresh job never got a ChargedAt and the
        // monthly count is unchanged (the job never reaches 'queued').
        await AssertChargedNonFailedCountAsync(userId, CreatorAllowance);
        var freshJob = await GetJobAsync(fresh);
        freshJob.ChargedAt.Should().BeNull("a denied charge must not stamp the ledger");
        freshJob.Status.Should().Be("validated", "the job must not advance to 'queued' without a credit");
    }

    // ── 3. A failed job releases its credit (excluded from the monthly used-count) ──

    [Fact]
    public async Task FailedJob_IsExcludedFromMonthlyCount_AndReleasesTheCredit()
    {
        var (userId, _) = await SeedCreatorAsync();
        var jobId = await SeedJobAsync(userId, status: "validated");

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var credits = scope.ServiceProvider.GetRequiredService<IReleaseCreditService>();
            (await credits.TryChargeAsync(jobId, userId)).Should().BeTrue();

            var afterCharge = await credits.GetStatusAsync(userId);
            afterCharge.Used.Should().Be(1);
            afterCharge.Remaining.Should().Be(CreatorAllowance - 1);
        }

        // The worker exhausts its retry and the job terminally fails.
        await SetJobStatusAsync(jobId, "failed");

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var credits = scope.ServiceProvider.GetRequiredService<IReleaseCreditService>();
            var afterFailure = await credits.GetStatusAsync(userId);

            afterFailure.Used.Should().Be(0, "a failed job is excluded from the monthly used-count");
            afterFailure.Remaining.Should().Be(CreatorAllowance, "the credit is recovered (the audit row stays)");
        }

        // The ChargedAt audit row is retained even though the credit was released.
        var failedJob = await GetJobAsync(jobId);
        failedJob.Status.Should().Be("failed");
        failedJob.ChargedAt.Should().NotBeNull("the audit row remains for accounting history");

        // Repository-level count confirms the failed job is filtered out.
        await AssertChargedNonFailedCountAsync(userId, 0);
    }

    // ── 4. Atomic charge: concurrent charges with one credit left → exactly one success ──

    [Fact]
    public async Task ConcurrentCharges_WithOneCreditLeft_ExactlyOneSucceeds()
    {
        var (userId, _) = await SeedCreatorAsync();

        // Burn all but one credit, then race several charges against the last one.
        for (var i = 0; i < CreatorAllowance - 1; i++)
            await SeedJobAsync(userId, status: "queued", chargedAt: DateTime.UtcNow);

        const int contenders = 4;
        var jobIds = new List<Guid>();
        for (var i = 0; i < contenders; i++)
            jobIds.Add(await SeedJobAsync(userId, status: "validated"));

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var credits = scope.ServiceProvider.GetRequiredService<IReleaseCreditService>();
            (await credits.GetStatusAsync(userId)).Remaining.Should().Be(1, "exactly one credit is left to race for");
        }

        // Each contender runs in its OWN DI scope (own DbContext + transaction) so this
        // is genuine concurrency, not serialized work on a shared context.
        var successes = 0;
        await Parallel.ForEachAsync(
            jobIds,
            new ParallelOptions { MaxDegreeOfParallelism = contenders },
            async (jobId, ct) =>
            {
                await using var scope = _fixture.Services.CreateAsyncScope();
                var credits = scope.ServiceProvider.GetRequiredService<IReleaseCreditService>();
                try
                {
                    if (await credits.TryChargeAsync(jobId, userId, ct))
                        Interlocked.Increment(ref successes);
                }
                catch (DbUpdateException)
                {
                    // A serialization/concurrency conflict counts as a non-success — the
                    // invariant is that the persisted ledger never exceeds the allowance.
                }
                catch (InvalidOperationException)
                {
                    // Transient provider-level transaction contention (e.g. SQLite lock).
                }
            });

        successes.Should().Be(1, "with one credit left, exactly one concurrent charge may win");

        // The persisted state is the real invariant: the month's charged, non-failed
        // count must land exactly on the allowance — never over-charged.
        await AssertChargedNonFailedCountAsync(userId, CreatorAllowance);

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var credits = scope.ServiceProvider.GetRequiredService<IReleaseCreditService>();
            (await credits.GetStatusAsync(userId)).Remaining.Should().Be(0);
        }
    }

    // ── 5. Monthly usage resets at the UTC month boundary (injected clock) ──

    [Fact]
    public async Task MonthlyUsage_ResetsAtUtcMonthBoundary_NoRollover()
    {
        var (userId, _) = await SeedCreatorAsync();
        // A credit charged on 15 Jan 2026 (UTC).
        await SeedJobAsync(userId, status: "queued",
            chargedAt: new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc));

        // Clock in January → the charge is counted.
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var jan = BuildService(scope.ServiceProvider, At(2026, 1, 20));
            var s = await jan.GetStatusAsync(userId);
            s.Used.Should().Be(1, "the 15 Jan charge falls inside January");
            s.Remaining.Should().Be(CreatorAllowance - 1);
        }

        // Clock rolls into February → January's charge no longer counts: credits
        // reset for the new calendar month and never roll over.
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var feb = BuildService(scope.ServiceProvider, At(2026, 2, 1));
            var s = await feb.GetStatusAsync(userId);
            s.Used.Should().Be(0, "a new calendar month resets usage — credits do not roll over");
            s.Remaining.Should().Be(CreatorAllowance);
        }
    }

    // ── 6. The month boundary is inclusive of 00:00:00 UTC on the 1st ──

    [Fact]
    public async Task MonthBoundary_IsInclusiveAtMidnightUtcFirst()
    {
        var (userId, _) = await SeedCreatorAsync();
        // One charge at the last instant of January, one at the first instant of February.
        await SeedJobAsync(userId, status: "queued",
            chargedAt: new DateTime(2026, 1, 31, 23, 59, 59, DateTimeKind.Utc));
        await SeedJobAsync(userId, status: "queued",
            chargedAt: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        await using var scope = _fixture.Services.CreateAsyncScope();
        var feb = BuildService(scope.ServiceProvider, At(2026, 2, 15));
        var s = await feb.GetStatusAsync(userId);

        s.Used.Should().Be(1,
            "only the 1 Feb 00:00:00 charge is in February; the 31 Jan 23:59:59 charge is excluded");
        s.Remaining.Should().Be(CreatorAllowance - 1);
    }

    // ── 7. Monthly grant is differentiated by tier (Free 0 / Creator 3 / Pro 10) ──

    [Fact]
    public async Task GrantByTier_CreatorGetsThree_ProGetsTen()
    {
        // The money config is the source of truth — pin it so a stray edit can't
        // silently change what a paying tier is owed.
        TierManifest.For(CreatorTier.Free).ReleaseReadyCreditsPerMonth.Should().Be(0);
        TierManifest.For(CreatorTier.Creator).ReleaseReadyCreditsPerMonth.Should().Be(CreatorAllowance);
        TierManifest.For(CreatorTier.Pro).ReleaseReadyCreditsPerMonth.Should().Be(ProAllowance);

        // And prove it end-to-end through the credit service, which resolves the
        // allowance from the user's authoritative CreatorTier — not a hardcoded number.
        var (creatorId, _) = await SeedUserWithTierAsync(CreatorTier.Creator);
        var (proId, _) = await SeedUserWithTierAsync(CreatorTier.Pro);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var credits = scope.ServiceProvider.GetRequiredService<IReleaseCreditService>();

        var creator = await credits.GetStatusAsync(creatorId);
        creator.Allowance.Should().Be(CreatorAllowance);
        creator.Remaining.Should().Be(CreatorAllowance, "an unused Creator starts with their full grant");
        creator.Plan.Should().Be("creator");

        var pro = await credits.GetStatusAsync(proId);
        pro.Allowance.Should().Be(ProAllowance, "Pro is granted 10 Release Ready masters per month");
        pro.Remaining.Should().Be(ProAllowance, "an unused Pro starts with their full grant");
        pro.Plan.Should().Be("pro");
    }

    // ── 8. A corrupt upload fails mastering and consumes NO credit (integration) ──
    // Ties the "failed-jobs-release-the-credit" rule to the REAL ffmpeg engine: garbage
    // bytes must fail mastering rather than yield a chargeable "success", and the failed
    // job releases its credit.

    [Fact]
    public async Task CorruptAudio_FailsMastering_AndConsumesNoCredit()
    {
        var (userId, _) = await SeedCreatorAsync();
        var jobId = await SeedJobAsync(userId, status: "validated");

        // 1) Submit charges the credit up front (ChargedAt stamped, job -> queued).
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var credits = scope.ServiceProvider.GetRequiredService<IReleaseCreditService>();
            (await credits.TryChargeAsync(jobId, userId)).Should().BeTrue();
            (await credits.GetStatusAsync(userId)).Used.Should().Be(1, "submit reserves one credit");
        }

        // 2) The REAL ffmpeg engine handed corrupt bytes must THROW — never return a
        //    successful master. (Holds whether ffmpeg rejects the stream with a non-zero
        //    exit or is unavailable; either way a bad upload is never billed as good.)
        var engine = new FfmpegEngine(Options.Create(new MasteringOptions()), NullLogger<FfmpegEngine>.Instance);
        using var corrupt = new MemoryStream(Encoding.ASCII.GetBytes("not audio — corrupt garbage bytes"));
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var master = async () => await engine.MasterAsync(
            new MasteringEngineRequest
            {
                Source = corrupt,
                SourceFileName = "corrupt.wav",
                TargetLufs = -14.0,
                TargetTruePeakDbtp = -1.0,
            },
            guard.Token);
        await master.Should().ThrowAsync<Exception>(
            "corrupt input must fail mastering, never silently succeed and bill the creator");

        // 3) The worker's terminal-failure transition marks the job failed...
        await SetJobStatusAsync(jobId, "failed");

        // 4) ...which releases the credit — a failed job is excluded from the monthly count.
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var credits = scope.ServiceProvider.GetRequiredService<IReleaseCreditService>();
            var status = await credits.GetStatusAsync(userId);
            status.Used.Should().Be(0, "a corrupt upload that fails mastering consumes no credit");
            status.Remaining.Should().Be(CreatorAllowance, "the reserved credit is fully recovered");
        }

        // The audit row is retained for accounting history even though the credit is released.
        (await GetJobAsync(jobId)).ChargedAt.Should().NotBeNull();
    }

    // ── Purchased credit packs (never-expiring pool, spent after monthly) ──

    [Fact]
    public async Task PurchasedCredits_SpendAfterMonthly_AndAppearInStatus()
    {
        var (userId, _) = await SeedCreatorAsync();                       // monthly allowance 3
        await SeedPurchaseAsync(userId, credits: 2, sessionId: $"cs_{Guid.NewGuid():N}");

        await using var scope = _fixture.Services.CreateAsyncScope();
        var credits = scope.ServiceProvider.GetRequiredService<IReleaseCreditService>();

        var before = await credits.GetStatusAsync(userId);
        before.MonthlyRemaining.Should().Be(CreatorAllowance);
        before.Purchased.Should().Be(2);
        before.Remaining.Should().Be(CreatorAllowance + 2, "spendable = monthly remaining + purchased");

        // Exhaust the monthly allowance — each of these must draw from the monthly pool.
        for (var i = 0; i < CreatorAllowance; i++)
        {
            var jobId = await SeedJobAsync(userId, status: "validated");
            (await credits.TryChargeAsync(jobId, userId)).Should().BeTrue();
            (await GetJobAsync(jobId)).CreditSource.Should().Be("monthly");
        }

        var afterMonthly = await credits.GetStatusAsync(userId);
        afterMonthly.MonthlyRemaining.Should().Be(0);
        afterMonthly.Purchased.Should().Be(2, "the purchased pool is untouched while monthly credits remained");
        afterMonthly.Remaining.Should().Be(2);

        // The next charge falls back to the purchased pool.
        var purchasedJob = await SeedJobAsync(userId, status: "validated");
        (await credits.TryChargeAsync(purchasedJob, userId)).Should().BeTrue();
        (await GetJobAsync(purchasedJob)).CreditSource.Should().Be("purchased");

        var afterPurchased = await credits.GetStatusAsync(userId);
        afterPurchased.Used.Should().Be(CreatorAllowance, "monthly usage stays capped at the allowance");
        afterPurchased.Purchased.Should().Be(1, "exactly one purchased credit was consumed");
        afterPurchased.Remaining.Should().Be(1);
    }

    [Fact]
    public async Task NoMonthlyAndNoPurchased_TryCharge_ReturnsFalse()
    {
        var (userId, _) = await SeedUserWithTierAsync(CreatorTier.Free);  // allowance 0, no packs
        await using var scope = _fixture.Services.CreateAsyncScope();
        var credits = scope.ServiceProvider.GetRequiredService<IReleaseCreditService>();

        var jobId = await SeedJobAsync(userId, status: "validated");
        (await credits.TryChargeAsync(jobId, userId)).Should().BeFalse("no monthly allowance and no purchased credits");
        (await GetJobAsync(jobId)).ChargedAt.Should().BeNull();
    }

    [Fact]
    public async Task CreateCreditCheckout_UsesServerResolvedPrice_AndReturnsUrl()
    {
        var (userId, _) = await SeedCreatorAsync();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var credits = scope.ServiceProvider.GetRequiredService<IReleaseCreditService>();

        var result = await credits.CreateCreditCheckoutAsync(userId, "triple");
        result.CheckoutUrl.Should().NotBeNullOrEmpty();

        // The $24 / 3-credit price is resolved on the server — the client never sets it.
        var session = _fixture.PaymentGateway.Sessions
            .Should().ContainSingle(s => s.ClientReferenceId == $"{userId}:credits:3").Which;
        session.AmountTotal.Should().Be(2400);
    }

    [Fact]
    public async Task CreateCreditCheckout_UnknownPack_Throws()
    {
        var (userId, _) = await SeedCreatorAsync();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var credits = scope.ServiceProvider.GetRequiredService<IReleaseCreditService>();

        var act = async () => await credits.CreateCreditCheckoutAsync(userId, "bogus-pack");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Seeding / assertion helpers ──

    private Task<(string userId, string email)> SeedCreatorAsync()
        => SeedUserWithTierAsync(CreatorTier.Creator);

    private async Task<(string userId, string email)> SeedUserWithTierAsync(CreatorTier tier)
    {
        var email = $"rr-credit-{Guid.NewGuid():N}@cambrian.com";
        await _fixture.RegisterUserAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);

        // Grant the tier's allowance directly on the user row (CreatorTier is the
        // authoritative entitlement the credit service reads).
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var user = await db.Users.FirstAsync(u => u.Id == userId);
        user.CreatorTier = tier;
        await db.SaveChangesAsync();

        return (userId, email);
    }

    private async Task<Guid> SeedJobAsync(string userId, string status, DateTime? chargedAt = null)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

        var jobId = Guid.NewGuid();
        db.MasteringJobs.Add(new MasteringJob
        {
            Id = jobId,
            CreatorId = userId,
            Engine = "ffmpeg",
            Status = status,
            SourceKey = $"release-ready/source/{jobId}.wav",
            SourceFileName = "audio.wav",
            ChargedAt = chargedAt,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return jobId;
    }

    private async Task SeedPurchaseAsync(string userId, int credits, string sessionId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        db.ReleaseCreditPurchases.Add(new ReleaseCreditPurchase
        {
            Id = Guid.NewGuid(),
            CreatorId = userId,
            Credits = credits,
            AmountCents = 0,
            Pack = "test",
            Status = "paid",
            StripeSessionId = sessionId,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SetJobStatusAsync(Guid jobId, string status)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var job = await db.MasteringJobs.FirstAsync(j => j.Id == jobId);
        job.Status = status;
        await db.SaveChangesAsync();
    }

    private async Task<MasteringJob> GetJobAsync(Guid jobId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        return await db.MasteringJobs.AsNoTracking().FirstAsync(j => j.Id == jobId);
    }

    private async Task AssertChargedNonFailedCountAsync(string userId, int expected)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var count = await db.MasteringJobs.CountAsync(
            j => j.CreatorId == userId
                 && j.ChargedAt != null
                 && j.ChargedAt >= monthStart
                 && j.Status != "failed");
        count.Should().Be(expected, "the persisted credit ledger must match the expected monthly usage");
    }

    private static DateTimeOffset At(int year, int month, int day)
        => new(year, month, day, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Build a ReleaseCreditService over the scope's real EF stack but with a
    /// fixed clock, so calendar-month logic can be exercised at arbitrary instants.</summary>
    private static ReleaseCreditService BuildService(IServiceProvider sp, DateTimeOffset now)
        => new(
            sp.GetRequiredService<UserManager<ApplicationUser>>(),
            sp.GetRequiredService<IMasteringJobRepository>(),
            sp.GetRequiredService<IReleaseCreditPurchaseRepository>(),
            sp.GetRequiredService<IPaymentGateway>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ITransactionManager>(),
            new FixedClock(now),
            NullLogger<ReleaseCreditService>.Instance);

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
