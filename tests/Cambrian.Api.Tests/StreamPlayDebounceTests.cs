using Cambrian.Api.Tests.Fixtures;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests;

/// <summary>
/// Rapid repeat starts collapse into one pending playback session. They do not
/// mutate the qualified-play ledger or lifetime projection.
/// </summary>
public sealed class StreamPlayDebounceTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public StreamPlayDebounceTests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Start_RapidRepeatSameUserTrack_ReusesPendingSessionWithoutCounting()
    {
        var ownerEmail = $"debounce-owner-{Guid.NewGuid():N}@test.com";
        var listenerEmail = $"debounce-listener-{Guid.NewGuid():N}@test.com";
        await _fixture.RegisterUserAsync(ownerEmail);
        var token = await _fixture.RegisterUserAsync(listenerEmail);
        var ownerId = await _fixture.GetUserIdAsync(ownerEmail);
        var firstTrackId = await _fixture.SeedTrackAsync(ownerId, "First pending track");
        var secondTrackId = await _fixture.SeedTrackAsync(ownerId, "Second pending track");
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var first = await StartAsync(client, firstTrackId);
        var repeated = await StartAsync(client, firstTrackId);
        var otherTrack = await StartAsync(client, secondTrackId);

        Assert.Equal("started", first.GetProperty("status").GetString());
        Assert.Equal("already_started", repeated.GetProperty("status").GetString());
        Assert.Equal(first.GetProperty("streamId").GetString(), repeated.GetProperty("streamId").GetString());
        Assert.NotEqual(first.GetProperty("streamId").GetString(), otherTrack.GetProperty("streamId").GetString());

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        Assert.Equal(2, await db.StreamSessions.CountAsync(s => s.TrackId == firstTrackId || s.TrackId == secondTrackId));
        Assert.False(await db.QualifiedPlayEvents.AnyAsync(e => e.TrackId == firstTrackId || e.TrackId == secondTrackId));
        Assert.False(await db.TrackStats.AnyAsync(s => s.TrackId == firstTrackId || s.TrackId == secondTrackId));
    }

    [Fact]
    public async Task Start_AnonymousIdentity_DedupesOnlyMatchingStableIdentifier()
    {
        var ownerEmail = $"anon-debounce-owner-{Guid.NewGuid():N}@test.com";
        await _fixture.RegisterUserAsync(ownerEmail);
        var ownerId = await _fixture.GetUserIdAsync(ownerEmail);
        var trackId = await _fixture.SeedTrackAsync(ownerId, "Anonymous pending track");

        var anonymousA = _fixture.CreateClient();
        anonymousA.DefaultRequestHeaders.Add("X-Cambrian-Anonymous-Session", "anon-a");
        var firstA = await StartAsync(anonymousA, trackId);
        var repeatedA = await StartAsync(anonymousA, trackId);

        var anonymousB = _fixture.CreateClient();
        anonymousB.DefaultRequestHeaders.Add("X-Cambrian-Anonymous-Session", "anon-b");
        var firstB = await StartAsync(anonymousB, trackId);

        Assert.Equal(firstA.GetProperty("streamId").GetString(), repeatedA.GetProperty("streamId").GetString());
        Assert.NotEqual(firstA.GetProperty("streamId").GetString(), firstB.GetProperty("streamId").GetString());

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        Assert.Equal(2, await db.StreamSessions.CountAsync(s => s.TrackId == trackId));
        Assert.False(await db.QualifiedPlayEvents.AnyAsync(e => e.TrackId == trackId));
    }

    private static async Task<JsonElement> StartAsync(HttpClient client, Guid trackId)
    {
        var response = await client.PostAsJsonAsync("/stream/start", new { trackId = trackId.ToString("D") });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("data").Clone();
    }
}
