using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cambrian.Api.Tests;

/// <summary>
/// Weekly Scene chart persistence (creator-audit fix 9):
///  - ranking uses qualified plays INSIDE the chart week, not raw sessions or
///    all-time popularity;
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

    private async Task SeedQualifiedPlaysAsync(Guid trackId, DateTime qualifiedAtUtc, int count)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var track = await db.Tracks.SingleAsync(item => item.Id == trackId);

        for (var i = 0; i < count; i++)
        {
            var eventTime = qualifiedAtUtc.AddMilliseconds(i);
            var sessionId = Guid.NewGuid();
            db.StreamSessions.Add(new StreamSession
            {
                Id = sessionId,
                TrackId = trackId,
                StartedAt = eventTime.AddSeconds(-30),
                StoppedAt = eventTime,
                ActivePlaybackSeconds = 30,
                QualificationThresholdSeconds = 30,
                QualificationStatus = "qualified",
                QualifiedAtUtc = eventTime,
                WasEligibleAtStart = true,
            });
            db.QualifiedPlayEvents.Add(new QualifiedPlayEvent
            {
                Id = Guid.NewGuid(),
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                TrackId = trackId,
                CreatorId = track.CreatorId,
                ListenerKeyHash = Guid.NewGuid().ToString("N"),
                PlaybackSessionId = sessionId,
                QualifiedAtUtc = eventTime,
                QualificationBasis = "active_playback_threshold",
                ActivePlaybackSeconds = 30,
                ThresholdSeconds = 30,
                CreatedAtUtc = eventTime,
                AggregatedAtUtc = eventTime,
            });
        }

        var stats = await db.TrackStats.SingleOrDefaultAsync(item => item.TrackId == trackId);
        if (stats is null)
        {
            stats = new TrackStat { TrackId = trackId };
            db.TrackStats.Add(stats);
        }

        stats.QualifiedPlayCount += count;
        stats.PlayCount += count;
        stats.LastPlayedAt = qualifiedAtUtc.AddMilliseconds(Math.Max(0, count - 1));
        stats.UpdatedAt = DateTime.UtcNow;
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
        await SeedQualifiedPlaysAsync(playedThisWeek, weekStart.AddHours(1), 5);
        await SeedQualifiedPlaysAsync(playedLastMonth, weekStart.AddDays(-30), 50);

        var res = await admin.PostAsync("/admin/charts/aggregate", null);
        res.EnsureSuccessStatusCode();
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

        Assert.Equal("weekly_plays", data.GetProperty("basis").GetString());

        var entries = data.GetProperty("entries").EnumerateArray().ToList();
        var hotRank = entries.First(e => e.GetProperty("trackId").GetString() == playedThisWeek.ToString())
            .GetProperty("rank").GetInt32();
        var hot = entries.First(e => e.GetProperty("trackId").GetString() == playedThisWeek.ToString());
        var old = entries.First(e => e.GetProperty("trackId").GetString() == playedLastMonth.ToString());
        var oldRank = old.GetProperty("rank").GetInt32();

        Assert.True(hotRank < oldRank,
            $"in-window plays must outrank stale all-time plays (hot={hotRank}, old={oldRank})");
        Assert.Equal(5, hot.GetProperty("weeklyQualifiedPlays").GetInt64());
        Assert.Equal(5, hot.GetProperty("rankingScore").GetInt64());
        Assert.Equal(50, old.GetProperty("lifetimePlays").GetInt64());
        Assert.Equal(0, old.GetProperty("weeklyQualifiedPlays").GetInt64());
    }

    [Fact]
    public async Task Weekly_window_is_half_open_and_uses_qualified_at_timestamp()
    {
        var admin = await CreateAdminAsync("boundary");
        var creatorId = await _fixture.GetUserIdAsync("chart-admin-boundary@test.com");
        var trackId = await _fixture.SeedTrackAsync(creatorId, "Boundary track");
        var weekStart = StartOfIsoWeekUtc(DateTime.UtcNow);

        await SeedQualifiedPlaysAsync(trackId, weekStart, 1);
        await SeedQualifiedPlaysAsync(trackId, weekStart.AddDays(7), 1);

        var response = await admin.PostAsync("/admin/charts/aggregate", null);
        response.EnsureSuccessStatusCode();
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var entry = data.GetProperty("entries")
            .EnumerateArray()
            .Single(item => item.GetProperty("trackId").GetString() == trackId.ToString());

        Assert.Equal(1, entry.GetProperty("weeklyQualifiedPlays").GetInt64());
        Assert.Equal(2, entry.GetProperty("lifetimePlays").GetInt64());
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
        await SeedQualifiedPlaysAsync(trackId, weekStart.AddHours(2), 25);

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
        Assert.Equal("weekly_plays", basis.GetString());
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("weekOf").GetString()));
        Assert.True(data.TryGetProperty("chartWindowStart", out _));
        Assert.True(data.TryGetProperty("chartWindowEnd", out _));
        Assert.True(data.TryGetProperty("generatedAt", out _));
        Assert.True(data.TryGetProperty("dataThrough", out var dataThrough));
        Assert.NotEqual(JsonValueKind.Null, dataThrough.ValueKind);
        Assert.True(data.TryGetProperty("isStale", out _));
    }

    [Fact]
    public async Task Ranking_uses_required_tie_breakers_and_excludes_ineligible_tracks()
    {
        var admin = await CreateAdminAsync("eligibility");
        var creatorId = await _fixture.GetUserIdAsync("chart-admin-eligibility@test.com");
        var newer = await _fixture.SeedTrackAsync(creatorId, "Newer tie");
        var tieA = await _fixture.SeedTrackAsync(creatorId, "GUID tie A");
        var tieB = await _fixture.SeedTrackAsync(creatorId, "GUID tie B");
        var zeroPlay = await _fixture.SeedTrackAsync(creatorId, "Eligible zero-play track");

        var hidden = await _fixture.SeedTrackAsync(creatorId, "Hidden", "hidden");
        var unavailable = await _fixture.SeedTrackAsync(creatorId, "Unavailable");
        var deleted = await _fixture.SeedTrackAsync(creatorId, "Deleted");
        var purged = await _fixture.SeedTrackAsync(creatorId, "Purged");
        var exclusive = await _fixture.SeedTrackAsync(creatorId, "Exclusive");
        var noAudio = await _fixture.SeedTrackAsync(creatorId, "No audio");

        var commonCreatedAt = DateTime.UtcNow.AddDays(-3);
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var tracks = await db.Tracks
                .Where(track => new[]
                {
                    newer, tieA, tieB, zeroPlay, hidden, unavailable,
                    deleted, purged, exclusive, noAudio,
                }.Contains(track.Id))
                .ToDictionaryAsync(track => track.Id);

            tracks[newer].CreatedAt = commonCreatedAt.AddMinutes(1);
            tracks[tieA].CreatedAt = commonCreatedAt;
            tracks[tieB].CreatedAt = commonCreatedAt;
            tracks[zeroPlay].CreatedAt = commonCreatedAt.AddDays(-1);
            tracks[unavailable].Status = "draft";
            tracks[deleted].DeletedAt = DateTime.UtcNow;
            tracks[purged].PurgedAt = DateTime.UtcNow;
            tracks[exclusive].ExclusiveSold = true;
            tracks[noAudio].AudioUrl = "   ";
            await db.SaveChangesAsync();
        }

        var weekStart = StartOfIsoWeekUtc(DateTime.UtcNow);
        foreach (var eligible in new[] { newer, tieA, tieB })
        {
            await SeedQualifiedPlaysAsync(eligible, weekStart.AddHours(3), 3);
        }

        foreach (var excluded in new[] { hidden, unavailable, deleted, purged, exclusive, noAudio })
        {
            await SeedQualifiedPlaysAsync(excluded, weekStart.AddHours(4), 4);
        }

        var response = await admin.PostAsync("/admin/charts/aggregate", null);
        response.EnsureSuccessStatusCode();
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var entries = data.GetProperty("entries").EnumerateArray().ToList();
        var orderedIds = entries
            .Select(entry => Guid.Parse(entry.GetProperty("trackId").GetString()!))
            .ToList();

        Assert.True(orderedIds.IndexOf(newer) < orderedIds.IndexOf(tieA));
        Assert.True(orderedIds.IndexOf(newer) < orderedIds.IndexOf(tieB));

        var expectedGuidOrder = new[] { tieA, tieB }.OrderBy(id => id).ToArray();
        var actualGuidOrder = new[] { tieA, tieB }.OrderBy(id => orderedIds.IndexOf(id)).ToArray();
        Assert.Equal(expectedGuidOrder, actualGuidOrder);
        Assert.Contains(zeroPlay, orderedIds);

        foreach (var excluded in new[] { hidden, unavailable, deleted, purged, exclusive, noAudio })
        {
            Assert.DoesNotContain(excluded, orderedIds);
        }
    }

    [Fact]
    public async Task Public_read_recomputes_when_snapshot_is_behind_qualified_events()
    {
        var admin = await CreateAdminAsync("behind");
        var creatorId = await _fixture.GetUserIdAsync("chart-admin-behind@test.com");
        var trackId = await _fixture.SeedTrackAsync(creatorId, "Behind watermark");

        var initial = await admin.PostAsync("/admin/charts/aggregate", null);
        initial.EnsureSuccessStatusCode();

        var weekStart = StartOfIsoWeekUtc(DateTime.UtcNow);
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var rows = await db.WeeklyChartSnapshots
                .Where(snapshot => snapshot.WeekStartUtc == weekStart)
                .ToListAsync();
            foreach (var row in rows)
            {
                row.DataThroughUtc = weekStart;
                row.ComputedAtUtc = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();
        }

        await SeedQualifiedPlaysAsync(trackId, weekStart.AddHours(1), 1);

        var response = await _fixture.CreateClient().GetAsync("/api/charts/weekly");
        response.EnsureSuccessStatusCode();
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var track = data.GetProperty("entries")
            .EnumerateArray()
            .Single(entry => entry.GetProperty("trackId").GetString() == trackId.ToString());

        Assert.Equal(1, track.GetProperty("weeklyQualifiedPlays").GetInt64());
        Assert.False(data.GetProperty("isStale").GetBoolean());
        Assert.True(data.GetProperty("dataThrough").GetDateTime() > weekStart);
    }

    [Fact]
    public async Task Empty_snapshot_metadata_marker_never_leaks_as_a_chart_entry()
    {
        var weekStart = StartOfIsoWeekUtc(DateTime.UtcNow);
        var now = DateTime.UtcNow;
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var existing = await db.WeeklyChartSnapshots
                .Where(snapshot => snapshot.WeekStartUtc == weekStart)
                .ToListAsync();
            db.WeeklyChartSnapshots.RemoveRange(existing);
            db.WeeklyChartSnapshots.Add(new WeeklyChartSnapshot
            {
                Id = Guid.NewGuid(),
                WeekStartUtc = weekStart,
                WeekEndUtc = weekStart.AddDays(7),
                Rank = 0,
                TrackId = Guid.Empty,
                CreatorId = string.Empty,
                Title = string.Empty,
                Artist = string.Empty,
                Basis = "weekly_plays",
                DataThroughUtc = now,
                ComputedAtUtc = now,
            });
            await db.SaveChangesAsync();
        }

        var response = await _fixture.CreateClient().GetAsync("/api/charts/weekly");
        response.EnsureSuccessStatusCode();
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

        Assert.Empty(data.GetProperty("entries").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("trackOfTheWeek").ValueKind);
    }

    [Fact]
    public async Task Older_concurrent_calculation_cannot_replace_newer_snapshot()
    {
        var weekStart = StartOfIsoWeekUtc(DateTime.UtcNow);
        var newerWatermark = DateTime.UtcNow;
        var newer = Snapshot("Newer calculation", newerWatermark, newerWatermark);
        // This calculation started with an older event watermark but finished
        // after the newer calculation. Finish time must not make stale data win.
        var older = Snapshot(
            "Older calculation that finished late",
            newerWatermark.AddMinutes(-1),
            newerWatermark.AddMinutes(1));

        using var scope = _fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWeeklyChartRepository>();
        await repo.ReplaceWeekAsync(weekStart, new[] { newer });
        await repo.ReplaceWeekAsync(weekStart, new[] { older });

        var persisted = await repo.GetWeekAsync(weekStart);
        var winner = Assert.Single(persisted);
        Assert.Equal("Newer calculation", winner.Title);
        Assert.Equal(newerWatermark, winner.DataThroughUtc);

        WeeklyChartSnapshot Snapshot(string title, DateTime dataThrough, DateTime computedAt) => new()
        {
            Id = Guid.NewGuid(),
            WeekStartUtc = weekStart,
            WeekEndUtc = weekStart.AddDays(7),
            Rank = 1,
            TrackId = Guid.NewGuid(),
            CreatorId = "concurrency-test",
            Title = title,
            Artist = "Test",
            Basis = "weekly_plays",
            DataThroughUtc = dataThrough,
            ComputedAtUtc = computedAt,
        };
    }
}
