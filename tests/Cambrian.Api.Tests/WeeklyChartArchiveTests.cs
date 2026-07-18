using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cambrian.Api.Tests;

/// <summary>
/// Weekly Scene chart ARCHIVE (SEO Task 1.3 — the Billboard model): every
/// completed week is a permanent, publicly readable record.
///  - the index lists completed weeks newest-first and NEVER the running week;
///  - a week page returns the final Top 50 with routable creator usernames;
///  - running/future/garbage week keys never resolve (permanent URLs only).
/// </summary>
public sealed class WeeklyChartArchiveTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public WeeklyChartArchiveTests(CambrianApiFixture fixture) => _fixture = fixture;

    private static DateTime StartOfIsoWeekUtc(DateTime utc)
    {
        var date = DateTime.SpecifyKind(utc.Date, DateTimeKind.Utc);
        int diff = (7 + (int)date.DayOfWeek - (int)DayOfWeek.Monday) % 7;
        return date.AddDays(-diff);
    }

    /// <summary>
    /// Seed the first-class Creators identity row (the fixture's SetUsernameAsync
    /// writes only AspNetUsers.UserName; /@handle routing — and therefore archive
    /// artist links — resolve exclusively through Creators.Username).
    /// </summary>
    private async Task SeedCreatorIdentityAsync(string userId, string username)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        if (!db.Creators.Any(c => c.UserId == userId))
        {
            db.Creators.Add(new Creator { Id = Guid.NewGuid(), UserId = userId, Username = username });
            await db.SaveChangesAsync();
        }
    }

    /// <summary>Seed a full persisted chart week directly (the worker's output shape).</summary>
    private async Task SeedWeekAsync(DateTime weekStartUtc, string creatorId, int entries, string titlePrefix)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        for (var rank = 1; rank <= entries; rank++)
        {
            db.WeeklyChartSnapshots.Add(new WeeklyChartSnapshot
            {
                Id = Guid.NewGuid(),
                WeekStartUtc = weekStartUtc,
                WeekEndUtc = weekStartUtc.AddDays(7),
                Rank = rank,
                TrackId = Guid.NewGuid(),
                CreatorId = creatorId,
                Title = $"{titlePrefix} #{rank}",
                Artist = "Archive Artist",
                PlaysInWindow = 100 - rank,
                Score = 100 - rank,
                Basis = "weekly_plays",
                ComputedAtUtc = weekStartUtc.AddDays(6),
            });
        }
        await db.SaveChangesAsync();
    }

    private static JsonElement DataOf(JsonElement root) => root.GetProperty("data");

    [Fact]
    public async Task Archive_index_lists_completed_weeks_newest_first_and_never_the_running_week()
    {
        var creator = await _fixture.CreateRoleClientAsync(
            "archive-index@test.com", "Test1234!@", "Creator", "archiveindex");
        var creatorId = await _fixture.GetUserIdAsync("archive-index@test.com");

        var currentWeek = StartOfIsoWeekUtc(DateTime.UtcNow);
        await SeedWeekAsync(currentWeek, creatorId, 3, "Running Week");
        await SeedWeekAsync(currentWeek.AddDays(-7), creatorId, 3, "Last Week");
        await SeedWeekAsync(currentWeek.AddDays(-14), creatorId, 3, "Two Weeks Ago");

        var anon = _fixture.CreateClient();
        var res = await anon.GetAsync("/api/charts/weekly/archive");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var weeks = DataOf(await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("weeks").EnumerateArray().ToList();

        var isoKeys = weeks.Select(w => w.GetProperty("isoWeek").GetString()).ToList();
        isoKeys.Should().Contain(WeeklyChartService.ToIsoWeekKey(currentWeek.AddDays(-7)));
        isoKeys.Should().Contain(WeeklyChartService.ToIsoWeekKey(currentWeek.AddDays(-14)));
        isoKeys.Should().NotContain(WeeklyChartService.ToIsoWeekKey(currentWeek),
            "the running week lives on /scene, never in the archive");

        // Newest first, and each summary names its #1.
        var weekOfs = weeks.Select(w => DateTime.Parse(w.GetProperty("weekOf").GetString()!)).ToList();
        weekOfs.Should().BeInDescendingOrder();
        var lastWeek = weeks.Single(w => w.GetProperty("isoWeek").GetString() == WeeklyChartService.ToIsoWeekKey(currentWeek.AddDays(-7)));
        lastWeek.GetProperty("topTrackTitle").GetString().Should().Be("Last Week #1");
    }

    [Fact]
    public async Task Archived_week_returns_the_full_final_chart_with_routable_usernames()
    {
        var creator = await _fixture.CreateRoleClientAsync(
            "archive-week@test.com", "Test1234!@", "Creator", "archiveweek");
        var creatorId = await _fixture.GetUserIdAsync("archive-week@test.com");
        await SeedCreatorIdentityAsync(creatorId, "archiveweek");

        var week = StartOfIsoWeekUtc(DateTime.UtcNow).AddDays(-21);
        await SeedWeekAsync(week, creatorId, 50, "Full Chart");

        var anon = _fixture.CreateClient();
        var res = await anon.GetAsync($"/api/charts/weekly/archive/{WeeklyChartService.ToIsoWeekKey(week)}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = DataOf(await res.Content.ReadFromJsonAsync<JsonElement>());
        var entries = data.GetProperty("entries").EnumerateArray().ToList();
        entries.Should().HaveCount(50);
        entries[0].GetProperty("rank").GetInt32().Should().Be(1);
        entries[0].GetProperty("title").GetString().Should().Be("Full Chart #1");
        entries[49].GetProperty("rank").GetInt32().Should().Be(50);
        // Artist links must be routable: username resolved at read time.
        entries[0].GetProperty("creatorUsername").GetString().Should().Be("archiveweek");
        data.GetProperty("weekOf").GetString().Should().StartWith(week.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public async Task Running_future_unknown_and_garbage_weeks_never_resolve()
    {
        var anon = _fixture.CreateClient();
        var currentWeek = StartOfIsoWeekUtc(DateTime.UtcNow);

        (await anon.GetAsync($"/api/charts/weekly/archive/{WeeklyChartService.ToIsoWeekKey(currentWeek)}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "the running week is not archived");
        (await anon.GetAsync($"/api/charts/weekly/archive/{WeeklyChartService.ToIsoWeekKey(currentWeek.AddDays(70))}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "future weeks are not archived");
        (await anon.GetAsync("/api/charts/weekly/archive/1999-w01"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "weeks with no snapshot are not archived");
        (await anon.GetAsync("/api/charts/weekly/archive/not-a-week"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "garbage keys are rejected, not treated as missing");
    }

    [Theory]
    [InlineData("2026-w28")]
    [InlineData("2026-W05")]
    public void Iso_week_keys_round_trip(string key)
    {
        var parsed = WeeklyChartService.ParseIsoWeekKey(key);
        parsed.Should().NotBeNull();
        parsed!.Value.DayOfWeek.Should().Be(DayOfWeek.Monday);
        WeeklyChartService.ToIsoWeekKey(parsed.Value).Should().Be(key.ToLowerInvariant());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026")]
    [InlineData("2026-28")]
    [InlineData("2026-w54")]
    [InlineData("w28-2026")]
    public void Garbage_iso_week_keys_parse_to_null(string? key)
    {
        WeeklyChartService.ParseIsoWeekKey(key).Should().BeNull();
    }
}
