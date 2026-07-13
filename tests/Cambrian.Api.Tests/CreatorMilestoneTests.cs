using System.Net;
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
/// Creator lifecycle milestones (release-gate task: first_play_received /
/// first_fan_event emitters). The dashboard exposes earliest-ever timestamps
/// so the frontend emitter can fire exactly once per creator:
///  - first play is the EARLIEST stream session (anonymous sessions count)
///    and never moves once set;
///  - first fan is the earliest of follow/save/support/subscription with a
///    source label;
///  - the payload carries timestamps + source + trackId ONLY — never a
///    listener or fan identity;
///  - the endpoint requires auth (nothing is exposed publicly).
/// </summary>
public sealed class CreatorMilestoneTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public CreatorMilestoneTests(CambrianApiFixture fixture) => _fixture = fixture;

    private async Task<(HttpClient Client, string UserId, Guid TrackId)> SeedCreatorAsync(string tag)
    {
        // The dashboard is Creator-gated — a plain registered user gets 403.
        var email = $"milestone-{tag}@test.com";
        var client = await _fixture.CreateRoleClientAsync(email, "Test1234!@", "Creator", $"milestone{tag}");
        var userId = await _fixture.GetUserIdAsync(email);
        var trackId = await _fixture.SeedTrackAsync(userId, $"Milestone Track {tag}");
        return (client, userId, trackId);
    }

    private static async Task<JsonElement> GetMilestonesAsync(HttpClient client)
    {
        var res = await client.GetAsync("/api/creators/dashboard");
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("data").GetProperty("milestones");
    }

    [Fact]
    public async Task Milestones_are_null_before_any_activity()
    {
        var (client, _, _) = await SeedCreatorAsync("empty");

        var milestones = await GetMilestonesAsync(client);

        Assert.Equal(JsonValueKind.Null, milestones.GetProperty("firstPlay").ValueKind);
        Assert.Equal(JsonValueKind.Null, milestones.GetProperty("firstFan").ValueKind);
    }

    [Fact]
    public async Task First_play_uses_the_earliest_anonymous_session_and_is_stable_across_reads()
    {
        var (client, _, trackId) = await SeedCreatorAsync("play");
        var earliest = DateTime.UtcNow.AddDays(-3);

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            // Two ANONYMOUS sessions (UserId null) — the milestone belongs to
            // the creator; listener identity is irrelevant and absent.
            db.StreamSessions.Add(new StreamSession { Id = Guid.NewGuid(), TrackId = trackId, UserId = null, StartedAt = earliest, IdempotencyKey = Guid.NewGuid().ToString(), Qualified = true });
            db.StreamSessions.Add(new StreamSession { Id = Guid.NewGuid(), TrackId = trackId, UserId = null, StartedAt = earliest.AddHours(5), IdempotencyKey = Guid.NewGuid().ToString(), Qualified = true });
            await db.SaveChangesAsync();
        }

        var first = await GetMilestonesAsync(client);
        var firstPlay = first.GetProperty("firstPlay");
        Assert.Equal(trackId.ToString(), firstPlay.GetProperty("trackId").GetString());
        var at1 = firstPlay.GetProperty("at").GetDateTime();
        Assert.Equal(earliest, at1, TimeSpan.FromSeconds(1));

        // Later plays never move the milestone — re-read is identical, which is
        // what makes the client-side emitter idempotent per creator.
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            db.StreamSessions.Add(new StreamSession { Id = Guid.NewGuid(), TrackId = trackId, UserId = null, StartedAt = DateTime.UtcNow, IdempotencyKey = Guid.NewGuid().ToString(), Qualified = true });
            await db.SaveChangesAsync();
        }

        var second = await GetMilestonesAsync(client);
        var at2 = second.GetProperty("firstPlay").GetProperty("at").GetDateTime();
        Assert.Equal(at1, at2);
    }

    [Fact]
    public async Task First_fan_event_picks_the_earliest_signal_with_its_source()
    {
        var (client, userId, trackId) = await SeedCreatorAsync("fan");
        var followAt = DateTime.UtcNow.AddDays(-2);
        var saveAt = DateTime.UtcNow.AddDays(-1);

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var creatorGuid = await db.Creators
                .Where(c => c.UserId == userId)
                .Select(c => (Guid?)c.Id)
                .FirstOrDefaultAsync();
            if (creatorGuid is null)
            {
                var creator = new Creator
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Username = $"milestone-fan-{Guid.NewGuid():N}",
                    DisplayName = "Milestone Fan Creator",
                };
                db.Creators.Add(creator);
                creatorGuid = creator.Id;
            }

            // Follow (earlier) + save/boost (later): follow must win.
            db.CreatorFollows.Add(new CreatorFollow { FollowerId = "some-listener", CreatorId = creatorGuid.Value, CreatedAt = followAt });
            db.TrackBoosts.Add(new TrackBoost { Id = Guid.NewGuid(), UserId = "some-listener", TrackId = trackId, CreatedAt = saveAt });
            await db.SaveChangesAsync();
        }

        var milestones = await GetMilestonesAsync(client);
        var firstFan = milestones.GetProperty("firstFan");
        Assert.Equal("follow", firstFan.GetProperty("source").GetString());
        Assert.Equal(followAt, firstFan.GetProperty("at").GetDateTime(), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Milestone_payload_never_contains_listener_or_fan_identity()
    {
        var (client, userId, trackId) = await SeedCreatorAsync("pii");
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            db.StreamSessions.Add(new StreamSession { Id = Guid.NewGuid(), TrackId = trackId, UserId = "listener-secret-id", StartedAt = DateTime.UtcNow.AddDays(-1), IdempotencyKey = Guid.NewGuid().ToString(), Qualified = true });
            var creatorGuid = await db.Creators
                .Where(c => c.UserId == userId)
                .Select(c => (Guid?)c.Id)
                .FirstOrDefaultAsync();
            if (creatorGuid is null)
            {
                var creator = new Creator
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Username = $"milestone-pii-{Guid.NewGuid():N}",
                    DisplayName = "PII Creator",
                };
                db.Creators.Add(creator);
                creatorGuid = creator.Id;
            }
            db.CreatorFollows.Add(new CreatorFollow { FollowerId = "fan-secret-id", CreatorId = creatorGuid.Value, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var milestones = await GetMilestonesAsync(client);

        // Exact property sets: nothing but at/trackId and at/source.
        var playProps = milestones.GetProperty("firstPlay").EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "at", "trackId" }, playProps);
        var fanProps = milestones.GetProperty("firstFan").EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "at", "source" }, fanProps);

        // Belt-and-braces: the raw payload never leaks the seeded identities.
        var raw = await (await client.GetAsync("/api/creators/dashboard")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("listener-secret-id", raw);
        Assert.DoesNotContain("fan-secret-id", raw);
    }

    [Fact]
    public async Task Dashboard_requires_authentication()
    {
        var anonymous = _fixture.CreateClient();
        var res = await anonymous.GetAsync("/api/creators/dashboard");
        Assert.True(
            res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"anonymous dashboard access must be denied, got {(int)res.StatusCode}");
    }
}
