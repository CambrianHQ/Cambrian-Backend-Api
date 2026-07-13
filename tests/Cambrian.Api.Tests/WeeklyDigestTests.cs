using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cambrian.Api.Tests;

/// <summary>
/// Weekly creator digest (creator-audit fix 10): skip policy (unverified /
/// opted-out / already-sent), dry-run sends nothing and stamps nothing, real
/// runs are per-user idempotent, the zero-stats template renders, and the
/// one-click unsubscribe endpoint flips the flag only with a valid HMAC token.
/// </summary>
public sealed class WeeklyDigestTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public WeeklyDigestTests(CambrianApiFixture fixture) => _fixture = fixture;

    private IWeeklyDigestService Digest()
        => _fixture.Services.GetRequiredService<IWeeklyDigestService>();

    private async Task<string> SeedCreatorWithTrackAsync(string tag, bool verified)
    {
        var email = $"digest-{tag}@test.com";
        if (verified)
            await _fixture.CreateAuthenticatedClientAsync(email, "Test1234!@");
        else
            await _fixture.CreateUnverifiedClientAsync(email, "Test1234!@");
        var userId = await _fixture.GetUserIdAsync(email);
        await _fixture.SeedTrackAsync(userId, $"Digest Track {tag}");
        return userId;
    }

    [Fact]
    public async Task DryRun_reports_recipients_but_stamps_nothing()
    {
        var userId = await SeedCreatorWithTrackAsync("dry", verified: true);

        var result = await Digest().RunAsync(dryRun: true);

        Assert.True(result.DryRun);
        Assert.Contains("digest-dry@test.com", result.Recipients);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId);
        Assert.Null(user.LastWeeklyDigestAtUtc); // dry-run never stamps
    }

    [Fact]
    public async Task Unverified_and_opted_out_creators_are_skipped()
    {
        await SeedCreatorWithTrackAsync("unverified", verified: false);
        var optedOutId = await SeedCreatorWithTrackAsync("optout", verified: true);

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var user = await db.Users.FirstAsync(u => u.Id == optedOutId);
            user.WeeklyDigestOptOut = true;
            await db.SaveChangesAsync();
        }

        var result = await Digest().RunAsync(dryRun: true);

        Assert.True(result.SkippedUnverified >= 1, "unverified creator must be skipped");
        Assert.True(result.SkippedOptedOut >= 1, "opted-out creator must be skipped");
        Assert.DoesNotContain("digest-unverified@test.com", result.Recipients);
        Assert.DoesNotContain("digest-optout@test.com", result.Recipients);
    }

    [Fact]
    public async Task Real_run_stamps_users_and_second_run_skips_them()
    {
        var userId = await SeedCreatorWithTrackAsync("idem", verified: true);

        // Testing env wires ConsoleEmailService — "sending" is a log-only no-op.
        var first = await Digest().RunAsync(dryRun: false);
        Assert.Contains("digest-idem@test.com", first.Recipients);

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId);
            Assert.NotNull(user.LastWeeklyDigestAtUtc);
        }

        var second = await Digest().RunAsync(dryRun: false);
        Assert.DoesNotContain("digest-idem@test.com", second.Recipients);
        Assert.True(second.SkippedAlreadySent >= 1, "second run must skip the stamped user");
    }

    [Fact]
    public async Task Weekly_numbers_count_only_the_reporting_window()
    {
        var userId = await SeedCreatorWithTrackAsync("window", verified: true);

        Guid trackId;
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            trackId = await db.Tracks.Where(t => t.CreatorId == userId).Select(t => t.Id).FirstAsync();

            var now = DateTime.UtcNow;
            var weekStart = now.Date.AddDays(-((7 + (int)now.DayOfWeek - (int)DayOfWeek.Monday) % 7));
            var reportFrom = weekStart.AddDays(-7); // digest reports the completed previous week

            db.StreamSessions.Add(new StreamSession { Id = Guid.NewGuid(), TrackId = trackId, StartedAt = reportFrom.AddHours(3), IdempotencyKey = Guid.NewGuid().ToString(), Qualified = true });
            db.StreamSessions.Add(new StreamSession { Id = Guid.NewGuid(), TrackId = trackId, StartedAt = reportFrom.AddHours(4), IdempotencyKey = Guid.NewGuid().ToString(), Qualified = true });
            // Outside the window (two months back) — must NOT count.
            db.StreamSessions.Add(new StreamSession { Id = Guid.NewGuid(), TrackId = trackId, StartedAt = reportFrom.AddDays(-60), IdempotencyKey = Guid.NewGuid().ToString(), Qualified = true });
            await db.SaveChangesAsync();
        }

        using var scope2 = _fixture.Services.CreateScope();
        var repo = scope2.ServiceProvider.GetRequiredService<IWeeklyDigestRepository>();
        var now2 = DateTime.UtcNow;
        var weekStart2 = now2.Date.AddDays(-((7 + (int)now2.DayOfWeek - (int)DayOfWeek.Monday) % 7));
        var numbers = await repo.GetWeeklyNumbersAsync(userId, weekStart2.AddDays(-7), weekStart2);

        Assert.Equal(2, numbers.Plays);
        Assert.Equal("Digest Track window", numbers.TopTrackTitle);
    }

    [Fact]
    public void Zero_stats_template_renders_without_blowing_up()
    {
        var html = WeeklyDigestService.BuildDigestHtml(
            new CreatorDigestStats { DisplayName = "Fresh Creator", Email = "x@test.com", UserId = "u1" },
            "https://api.example.com/email/unsubscribe?uid=u1&token=t");

        Assert.Contains("<strong>0</strong> plays", html);
        Assert.Contains("<strong>0</strong> new followers", html);
        Assert.Contains("No plays this week", html);
        Assert.Contains("Unsubscribe", html);
        Assert.Contains("Fresh Creator", html);
    }

    [Fact]
    public async Task Unsubscribe_endpoint_requires_valid_token_and_flips_flag()
    {
        var userId = await SeedCreatorWithTrackAsync("unsub", verified: true);
        var client = _fixture.CreateClient();

        var config = _fixture.Services.GetRequiredService<IConfiguration>();
        var goodToken = WeeklyDigestService.ComputeUnsubscribeToken(userId, config["Jwt:Key"] ?? string.Empty);

        // Wrong token → 400, flag untouched.
        var bad = await client.GetAsync($"/email/unsubscribe?uid={userId}&token=deadbeef");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // Valid token → 200 + flag set. Second call stays 200 (idempotent).
        var good = await client.GetAsync($"/email/unsubscribe?uid={userId}&token={goodToken}");
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        var again = await client.GetAsync($"/email/unsubscribe?uid={userId}&token={goodToken}");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId);
        Assert.True(user.WeeklyDigestOptOut);
    }

    [Fact]
    public async Task Admin_digest_trigger_is_admin_only_and_supports_dry_run()
    {
        var user = await _fixture.CreateAuthenticatedClientAsync("digest-nonadmin@test.com", "Test1234!@");
        var forbidden = await user.PostAsync("/admin/digest/run?dryRun=true", null);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var admin = await _fixture.CreateRoleClientAsync("digest-admin@test.com", "Test1234!@", "Admin", "digestadmin");
        var res = await admin.PostAsync("/admin/digest/run?dryRun=true", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.True(data.GetProperty("dryRun").GetBoolean());
    }
}
