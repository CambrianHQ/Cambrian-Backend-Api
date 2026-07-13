using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cambrian.Api.Tests;

/// <summary>
/// Weekly Scene chart persistence (creator-audit fix 9):
///  - ranking uses plays INSIDE the chart week, not all-time popularity;
///  - recompute is idempotent per week (no duplicate rows on re-run);
///  - movement deltas come from the previous week's persisted snapshot;
///  - the response declares its Basis so the frontend can label honestly.
/// </summary>
public sealed class WeeklyChartSnapshotTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public WeeklyChartSnapshotTests(CambrianApiFixture fixture) => _fixture = fixture;

    private static DateTime StartOfIsoWeekUtc(DateTime utc)
    {
        var date = DateTime.SpecifyKind(utc.Date, DateTimeKind.Utc);
        int diff = (7 + (int)date.DayOfWeek - (int)DayOfWeek.Monday) % 7;
        return date.AddDays(-diff);
    }

    private async Task SeedStreamSessionsAsync(Guid trackId, DateTime startedAtUtc, int count)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        for (var i = 0; i < count; i++)
        {
            db.StreamSessions.Add(new StreamSession
            {
                Id = Guid.NewGuid(),
                TrackId = trackId,
                StartedAt = startedAtUtc.AddMinutes(i),
                IdempotencyKey = Guid.NewGuid().ToString(),
                Qualified = true,
            });
        }
        await db.SaveChangesAsync();
    }

    private async Task<HttpClient> CreateAdminAsync(string tag)
    {
        return await _fixture.CreateRoleClientAsync(
            $"chart-admin-{tag}@test.com", "Test1234!@", "Admin", $"chartadmin{tag}");
    }

    [Fact]
    public async Task Recompute_is_idempotent_for_a_week_and_persists_rows()
    {
        var admin = await CreateAdminAsync("idem");
        var creatorId = await _fixture.GetUserIdAsync("chart-admin-idem@test.com");
        await _fixture.SeedTrackAsync(creatorId, "Idempotency Anthem");

        var first = await admin.PostAsync("/admin/charts/aggregate", null);
        first.EnsureSuccessStatusCode();
        var second = await admin.PostAsync("/admin/charts/aggregate", null);
        second.EnsureSuccessStatusCode();

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var weekStart = StartOfIsoWeekUtc(DateTime.UtcNow);

        var rows = await db.WeeklyChartSnapshots
            .Where(s => s.WeekStartUtc == weekStart)
            .ToListAsync();

        Assert.NotEmpty(rows);
        // No duplicate ranks or tracks — the week was REPLACED, not appended.
        Assert.Equal(rows.Count, rows.Select(r => r.Rank).Distinct().Count());
        Assert.Equal(rows.Count, rows.Select(r => r.TrackId).Distinct().Count());
    }

    [Fact]
    public async Task Weekly_chart_ranks_by_plays_inside_the_window_not_all_time()
    {
        var admin = await CreateAdminAsync("window");
        var creatorId = await _fixture.GetUserIdAsync("chart-admin-window@test.com");

        var playedThisWeek = await _fixture.SeedTrackAsync(creatorId, "Hot This Week");
        var playedLastMonth = await _fixture.SeedTrackAsync(creatorId, "Old All-Time Hit");

        var weekStart = StartOfIsoWeekUtc(DateTime.UtcNow);
        // 5 plays inside the current chart week vs 50 plays a month ago.
        await SeedStreamSessionsAsync(playedThisWeek, weekStart.AddHours(1), 5);
        await SeedStreamSessionsAsync(playedLastMonth, weekStart.AddDays(-30), 50);

        var res = await admin.PostAsync("/admin/charts/aggregate", null);
        res.EnsureSuccessStatusCode();
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

        Assert.Equal("weekly_plays", data.GetProperty("basis").GetString());

        var entries = data.GetProperty("entries").EnumerateArray().ToList();
        var hotRank = entries.First(e => e.GetProperty("trackId").GetString() == playedThisWeek.ToString())
            .GetProperty("rank").GetInt32();
        var oldRank = entries.First(e => e.GetProperty("trackId").GetString() == playedLastMonth.ToString())
            .GetProperty("rank").GetInt32();

        Assert.True(hotRank < oldRank,
            $"in-window plays must outrank stale all-time plays (hot={hotRank}, old={oldRank})");
    }

    [Fact]
    public async Task Movement_delta_comes_from_previous_week_snapshot_and_new_entries_have_none()
    {
        var admin = await CreateAdminAsync("delta");
        var creatorId = await _fixture.GetUserIdAsync("chart-admin-delta@test.com");
        var trackId = await _fixture.SeedTrackAsync(creatorId, "Mover");

        var weekStart = StartOfIsoWeekUtc(DateTime.UtcNow);

        // Persist a previous-week snapshot placing the track at rank 9.
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            db.WeeklyChartSnapshots.Add(new WeeklyChartSnapshot
            {
                Id = Guid.NewGuid(),
                WeekStartUtc = weekStart.AddDays(-7),
                WeekEndUtc = weekStart,
                Rank = 9,
                TrackId = trackId,
                CreatorId = creatorId,
                Title = "Mover",
                Artist = "Test",
                Basis = "weekly_plays",
                ComputedAtUtc = DateTime.UtcNow.AddDays(-7),
            });
            await db.SaveChangesAsync();
        }

        // Heavy in-window plays push it to (or near) the top this week.
        await SeedStreamSessionsAsync(trackId, weekStart.AddHours(2), 25);

        var res = await admin.PostAsync("/admin/charts/aggregate", null);
        res.EnsureSuccessStatusCode();
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var entries = data.GetProperty("entries").EnumerateArray().ToList();

        var mover = entries.First(e => e.GetProperty("trackId").GetString() == trackId.ToString());
        var rank = mover.GetProperty("rank").GetInt32();
        var delta = mover.GetProperty("deltaRank");

        Assert.NotEqual(JsonValueKind.Null, delta.ValueKind);
        Assert.Equal(9 - rank, delta.GetInt32());

        // A track with no previous-week row reports no movement (null delta) —
        // the frontend must not invent arrows for it.
        var newcomer = entries.FirstOrDefault(e => e.GetProperty("trackId").GetString() != trackId.ToString());
        if (newcomer.ValueKind == JsonValueKind.Object)
        {
            Assert.Equal(JsonValueKind.Null, newcomer.GetProperty("deltaRank").ValueKind);
        }
    }

    [Fact]
    public async Task Public_weekly_endpoint_serves_persisted_snapshot_with_basis()
    {
        var admin = await CreateAdminAsync("serve");
        await admin.PostAsync("/admin/charts/aggregate", null);

        var anonymous = _fixture.CreateClient();
        var res = await anonymous.GetAsync("/api/charts/weekly");
        res.EnsureSuccessStatusCode();

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.True(data.TryGetProperty("basis", out var basis), "weekly response must declare its basis");
        Assert.Contains(basis.GetString(), new[] { "weekly_plays", "catalog_trending" });
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("weekOf").GetString()));
    }
}
