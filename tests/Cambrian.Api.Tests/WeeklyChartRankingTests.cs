using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cambrian.Api.Tests;

/// <summary>
/// Shared test-data helpers for the deterministic-ranking suite below. Each
/// test class here gets its OWN isolated CambrianApiFixture/DB instance
/// (xUnit creates one fixture per test class), so tests that need an exact
/// candidate pool (e.g. absolute rank assertions) are given their own class
/// rather than sharing one with unrelated tests that also call /admin/charts/aggregate.
/// </summary>
file static class ChartTestHelpers
{
    public static async Task SeedStreamSessionsAsync(CambrianApiFixture fixture, Guid trackId, DateTime startedAtUtc, int count)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        for (var i = 0; i < count; i++)
        {
            db.StreamSessions.Add(new StreamSession
            {
                Id = Guid.NewGuid(),
                TrackId = trackId,
                StartedAt = startedAtUtc.AddSeconds(i),
            });
        }
        await db.SaveChangesAsync();
    }

    public static async Task SetTrackCreatedAtAsync(CambrianApiFixture fixture, Guid trackId, DateTime createdAtUtc)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var track = await db.Tracks.SingleAsync(t => t.Id == trackId);
        track.CreatedAt = createdAtUtc;
        await db.SaveChangesAsync();
    }

    public static async Task SuspendCreatorAsync(CambrianApiFixture fixture, string userId)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        user.Status = "suspended";
        await db.SaveChangesAsync();
    }

    public static int Rank(List<JsonElement> entries, Guid trackId) =>
        entries.Single(e => e.GetProperty("trackId").GetString() == trackId.ToString()).GetProperty("rank").GetInt32();

    public static async Task<JsonElement> AggregateAsync(HttpClient admin)
    {
        var res = await admin.PostAsync("/admin/charts/aggregate", null);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
    }
}

/// <summary>
/// Track A (5 plays) / B (3 plays) / C (1 play) must rank exactly 1 / 2 / 3.
/// Isolated in its own class/DB so no other test's plays can shift these
/// absolute rank positions.
/// </summary>
public sealed class WeeklyChartQualifiedPlayRankingTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;
    public WeeklyChartQualifiedPlayRankingTests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Tracks_rank_by_qualified_plays_descending()
    {
        var admin = await _fixture.CreateRoleClientAsync("chart-rank-abc@test.com", "Test1234!@", "Admin", "chartrankabc");
        var creatorId = await _fixture.GetUserIdAsync("chart-rank-abc@test.com");

        var trackA = await _fixture.SeedTrackAsync(creatorId, "Track A");
        var trackB = await _fixture.SeedTrackAsync(creatorId, "Track B");
        var trackC = await _fixture.SeedTrackAsync(creatorId, "Track C");

        var weekStart = WeeklyChartService.StartOfIsoWeekUtc(DateTime.UtcNow);
        await ChartTestHelpers.SeedStreamSessionsAsync(_fixture, trackA, weekStart.AddHours(1), 5);
        await ChartTestHelpers.SeedStreamSessionsAsync(_fixture, trackB, weekStart.AddHours(1), 3);
        await ChartTestHelpers.SeedStreamSessionsAsync(_fixture, trackC, weekStart.AddHours(1), 1);

        var data = await ChartTestHelpers.AggregateAsync(admin);
        var entries = data.GetProperty("entries").EnumerateArray().ToList();

        ChartTestHelpers.Rank(entries, trackA).Should().Be(1);
        ChartTestHelpers.Rank(entries, trackB).Should().Be(2);
        ChartTestHelpers.Rank(entries, trackC).Should().Be(3);

        var a = entries.Single(e => e.GetProperty("trackId").GetString() == trackA.ToString());
        a.GetProperty("playsInWindow").GetInt32().Should().Be(5);
        a.GetProperty("score").GetDouble().Should().Be(5);
    }
}

/// <summary>
/// Deterministic tie-break chain: score desc, qualified plays desc, publish
/// time desc, track id asc. These tests compare specific tracks' relative
/// order (not absolute rank numbers), so they're safe to share a class/DB.
/// </summary>
public sealed class WeeklyChartDeterministicTieBreakTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;
    public WeeklyChartDeterministicTieBreakTests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Tied_scores_break_by_publish_time_then_recompute_is_reproducible()
    {
        var admin = await _fixture.CreateRoleClientAsync("chart-tie@test.com", "Test1234!@", "Admin", "charttie");
        var creatorId = await _fixture.GetUserIdAsync("chart-tie@test.com");

        var older = await _fixture.SeedTrackAsync(creatorId, "Tied Older");
        var newer = await _fixture.SeedTrackAsync(creatorId, "Tied Newer");

        var weekStart = WeeklyChartService.StartOfIsoWeekUtc(DateTime.UtcNow);
        // Equal qualified plays — the tie must break on publish time, not luck.
        await ChartTestHelpers.SeedStreamSessionsAsync(_fixture, older, weekStart.AddHours(1), 4);
        await ChartTestHelpers.SeedStreamSessionsAsync(_fixture, newer, weekStart.AddHours(1), 4);
        await ChartTestHelpers.SetTrackCreatedAtAsync(_fixture, older, weekStart.AddDays(-30));
        await ChartTestHelpers.SetTrackCreatedAtAsync(_fixture, newer, weekStart.AddDays(-1));

        var data = await ChartTestHelpers.AggregateAsync(admin);
        var entries = data.GetProperty("entries").EnumerateArray().ToList();

        ChartTestHelpers.Rank(entries, newer).Should().BeLessThan(ChartTestHelpers.Rank(entries, older),
            "the newer-published track must win an exact qualified-plays tie");

        // Recomputing over unchanged data must reproduce the exact same order —
        // determinism, not an artifact of one lucky run.
        var data2 = await ChartTestHelpers.AggregateAsync(admin);
        var entries2 = data2.GetProperty("entries").EnumerateArray().ToList();
        ChartTestHelpers.Rank(entries2, newer).Should().Be(ChartTestHelpers.Rank(entries, newer));
        ChartTestHelpers.Rank(entries2, older).Should().Be(ChartTestHelpers.Rank(entries, older));
    }

    [Fact]
    public async Task Track_id_is_the_final_tiebreaker_when_score_and_publish_time_both_tie()
    {
        var admin = await _fixture.CreateRoleClientAsync("chart-finaltie@test.com", "Test1234!@", "Admin", "chartfinaltie");
        var creatorId = await _fixture.GetUserIdAsync("chart-finaltie@test.com");

        var one = await _fixture.SeedTrackAsync(creatorId, "Exact Tie One");
        var two = await _fixture.SeedTrackAsync(creatorId, "Exact Tie Two");

        var weekStart = WeeklyChartService.StartOfIsoWeekUtc(DateTime.UtcNow);
        var sameCreatedAt = weekStart.AddDays(-5);
        await ChartTestHelpers.SetTrackCreatedAtAsync(_fixture, one, sameCreatedAt);
        await ChartTestHelpers.SetTrackCreatedAtAsync(_fixture, two, sameCreatedAt);
        await ChartTestHelpers.SeedStreamSessionsAsync(_fixture, one, weekStart.AddHours(2), 2);
        await ChartTestHelpers.SeedStreamSessionsAsync(_fixture, two, weekStart.AddHours(2), 2);

        var data = await ChartTestHelpers.AggregateAsync(admin);
        var entries = data.GetProperty("entries").EnumerateArray().ToList();

        var expectedFirst = one.CompareTo(two) < 0 ? one : two;
        var expectedSecond = one.CompareTo(two) < 0 ? two : one;
        ChartTestHelpers.Rank(entries, expectedFirst).Should().BeLessThan(ChartTestHelpers.Rank(entries, expectedSecond),
            "with score and publish time both tied, the lower track id must win");
    }

    [Fact]
    public async Task Plays_at_week_start_count_plays_at_week_end_belong_to_next_week()
    {
        var admin = await _fixture.CreateRoleClientAsync("chart-boundary@test.com", "Test1234!@", "Admin", "chartboundary");
        var creatorId = await _fixture.GetUserIdAsync("chart-boundary@test.com");

        var atStart = await _fixture.SeedTrackAsync(creatorId, "At Week Start");
        var atEnd = await _fixture.SeedTrackAsync(creatorId, "At Week End");

        var weekStart = WeeklyChartService.StartOfIsoWeekUtc(DateTime.UtcNow);
        var weekEnd = weekStart.AddDays(7);
        await ChartTestHelpers.SeedStreamSessionsAsync(_fixture, atStart, weekStart, 1);   // inclusive lower bound
        await ChartTestHelpers.SeedStreamSessionsAsync(_fixture, atEnd, weekEnd, 1);       // exclusive upper bound

        var data = await ChartTestHelpers.AggregateAsync(admin);
        var entries = data.GetProperty("entries").EnumerateArray().ToList();

        entries.Should().Contain(e => e.GetProperty("trackId").GetString() == atStart.ToString(),
            "a play at exactly week-start is inside the window");
        entries.Should().NotContain(e => e.GetProperty("trackId").GetString() == atEnd.ToString(),
            "a play at exactly week-end belongs to the NEXT week, not this one");
    }
}

/// <summary>
/// Eligibility (charts-and-rankings audit, task 6): however many plays an
/// ineligible track has, it must never appear on the chart. Every assertion
/// here is an absence check on a specific track id, so it's safe to share a
/// class/DB with the other eligibility scenarios.
/// </summary>
public sealed class WeeklyChartEligibilityTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;
    public WeeklyChartEligibilityTests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Non_public_admin_hidden_flagged_and_removed_tracks_never_chart()
    {
        var admin = await _fixture.CreateRoleClientAsync("chart-elig@test.com", "Test1234!@", "Admin", "chartelig");
        var creatorId = await _fixture.GetUserIdAsync("chart-elig@test.com");

        var limited = await _fixture.SeedTrackAsync(creatorId, "Limited Visibility Track", visibility: "limited");
        var hidden = await _fixture.SeedTrackAsync(creatorId, "Admin Hidden Track");
        (await admin.PostAsync($"/admin/tracks/{hidden}/hide", null)).EnsureSuccessStatusCode();
        var flagged = await _fixture.SeedTrackAsync(creatorId, "Flagged Under Review");
        (await admin.PostAsync($"/admin/tracks/{flagged}/flag", null)).EnsureSuccessStatusCode();
        var removed = await _fixture.SeedTrackAsync(creatorId, "Removed Track");
        (await admin.PostAsync($"/admin/tracks/{removed}/remove", null)).EnsureSuccessStatusCode();

        var weekStart = WeeklyChartService.StartOfIsoWeekUtc(DateTime.UtcNow);
        var ineligible = new[] { limited, hidden, flagged, removed };
        foreach (var trackId in ineligible)
            await ChartTestHelpers.SeedStreamSessionsAsync(_fixture, trackId, weekStart.AddHours(1), 50); // huge play count — must still be excluded

        var data = await ChartTestHelpers.AggregateAsync(admin);
        var entries = data.GetProperty("entries").EnumerateArray().ToList();

        foreach (var trackId in ineligible)
            entries.Should().NotContain(e => e.GetProperty("trackId").GetString() == trackId.ToString());
    }

    [Fact]
    public async Task Tracks_from_a_suspended_creator_never_chart()
    {
        var admin = await _fixture.CreateRoleClientAsync("chart-elig-admin@test.com", "Test1234!@", "Admin", "chartsuspadmin");
        var creator = await _fixture.CreateRoleClientAsync("chart-elig-suspended@test.com", "Test1234!@", "Creator", "chartsuspcreator");
        var suspendedCreatorId = await _fixture.GetUserIdAsync("chart-elig-suspended@test.com");
        await ChartTestHelpers.SuspendCreatorAsync(_fixture, suspendedCreatorId);

        var track = await _fixture.SeedTrackAsync(suspendedCreatorId, "Suspended Creator's Track");
        var weekStart = WeeklyChartService.StartOfIsoWeekUtc(DateTime.UtcNow);
        await ChartTestHelpers.SeedStreamSessionsAsync(_fixture, track, weekStart.AddHours(1), 50);

        var data = await ChartTestHelpers.AggregateAsync(admin);
        var entries = data.GetProperty("entries").EnumerateArray().ToList();

        entries.Should().NotContain(e => e.GetProperty("trackId").GetString() == track.ToString());
    }
}

/// <summary>
/// Archive pagination stability: repeated calls, and different page sizes,
/// must never reorder or duplicate weeks. Seeds only completed (past) weeks
/// directly — never calls /admin/charts/aggregate, so it can't collide with
/// the running-week state any other test might create.
/// </summary>
public sealed class WeeklyChartArchivePaginationStabilityTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;
    public WeeklyChartArchivePaginationStabilityTests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Repeated_calls_and_smaller_limits_return_a_stable_prefix()
    {
        var creator = await _fixture.CreateRoleClientAsync("chart-page@test.com", "Test1234!@", "Creator", "chartpage");
        var creatorId = await _fixture.GetUserIdAsync("chart-page@test.com");
        var weekStart = WeeklyChartService.StartOfIsoWeekUtc(DateTime.UtcNow);

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            for (var i = 1; i <= 5; i++)
            {
                var thisWeekStart = weekStart.AddDays(-7 * i);
                db.WeeklyChartSnapshots.Add(new WeeklyChartSnapshot
                {
                    Id = Guid.NewGuid(),
                    WeekStartUtc = thisWeekStart,
                    WeekEndUtc = thisWeekStart.AddDays(7),
                    Rank = 1,
                    TrackId = Guid.NewGuid(),
                    CreatorId = creatorId,
                    Title = $"Page Week {i} #1",
                    Artist = "Pagination Artist",
                    Basis = "weekly_plays",
                    ComputedAtUtc = thisWeekStart.AddDays(6),
                });
            }
            await db.SaveChangesAsync();
        }

        var anon = _fixture.CreateClient();
        var full = await FetchIsoWeeksAsync(anon, "/api/charts/weekly/archive?limit=5");
        var smallPage = await FetchIsoWeeksAsync(anon, "/api/charts/weekly/archive?limit=2");
        var fullAgain = await FetchIsoWeeksAsync(anon, "/api/charts/weekly/archive?limit=5");

        full.Should().HaveCount(5);
        smallPage.Should().Equal(full.Take(2), "a smaller page must be a stable PREFIX of the larger page, not a differently-ordered subset");
        fullAgain.Should().Equal(full, "repeated calls over unchanged data must return byte-for-byte the same order every time");
    }

    private static async Task<List<string>> FetchIsoWeeksAsync(HttpClient client, string url)
    {
        var res = await client.GetAsync(url);
        res.EnsureSuccessStatusCode();
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        return data.GetProperty("weeks").EnumerateArray()
            .Select(w => w.GetProperty("isoWeek").GetString()!)
            .ToList();
    }
}

/// <summary>
/// Freshness/staleness reporting (charts-and-rankings audit, task 7 &amp; 8).
/// The integration test needs to be the sole writer of the running week's
/// snapshot state, so it gets its own class/DB.
/// </summary>
public sealed class WeeklyChartFreshnessTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;
    public WeeklyChartFreshnessTests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public void IsStale_boundary_is_exact_and_deterministic()
    {
        var computedAt = new DateTime(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);
        var interval = WeeklyChartService.RecomputeInterval;

        WeeklyChartService.IsStale(computedAt, computedAt).Should().BeFalse("freshly computed is never stale");
        WeeklyChartService.IsStale(computedAt, computedAt + interval).Should().BeFalse("one recompute interval of lag is expected jitter, not staleness");
        WeeklyChartService.IsStale(computedAt, computedAt + TimeSpan.FromTicks(interval.Ticks * 3)).Should().BeFalse("exactly at the threshold is not yet stale");
        WeeklyChartService.IsStale(computedAt, computedAt + TimeSpan.FromTicks(interval.Ticks * 3) + TimeSpan.FromSeconds(1)).Should().BeTrue("past the threshold must be reported stale");
    }

    [Fact]
    public async Task Serving_a_standin_older_week_is_always_reported_stale()
    {
        var creator = await _fixture.CreateRoleClientAsync("chart-fresh@test.com", "Test1234!@", "Creator", "chartfresh");
        var creatorId = await _fixture.GetUserIdAsync("chart-fresh@test.com");
        var weekStart = WeeklyChartService.StartOfIsoWeekUtc(DateTime.UtcNow);
        var lastWeekStart = weekStart.AddDays(-7);

        // Persist ONLY last week's snapshot — no row exists yet for the running week.
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            db.WeeklyChartSnapshots.Add(new WeeklyChartSnapshot
            {
                Id = Guid.NewGuid(),
                WeekStartUtc = lastWeekStart,
                WeekEndUtc = weekStart,
                Rank = 1,
                TrackId = Guid.NewGuid(),
                CreatorId = creatorId,
                Title = "Last Week Standin",
                Artist = "Freshness Artist",
                Basis = "weekly_plays",
                ComputedAtUtc = weekStart.AddDays(-1),
            });
            await db.SaveChangesAsync();
        }

        var anon = _fixture.CreateClient();
        var res = await anon.GetAsync("/api/charts/weekly");
        res.EnsureSuccessStatusCode();
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

        data.GetProperty("stale").GetBoolean().Should().BeTrue(
            "serving last week's chart as a stand-in for a not-yet-computed running week must be reported stale");
        data.GetProperty("weekOf").GetString().Should().StartWith(lastWeekStart.ToString("yyyy-MM-dd"));
    }
}
