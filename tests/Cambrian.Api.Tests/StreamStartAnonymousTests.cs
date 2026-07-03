using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests;

/// <summary>
/// F7/F1: anonymous listeners' plays are counted. POST /stream/start allows anonymous,
/// returns 2xx, records a StreamSession attributed to no user, and rate-limits to one
/// counted play per (track, client IP) per hour so repeat fire can't inflate the count.
/// </summary>
public sealed class StreamStartAnonymousTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public StreamStartAnonymousTests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task StreamStart_LoggedOut_CountsPlay_OncePerTrackPerHour()
    {
        var email = $"anonplay-{Guid.NewGuid():N}@cambrian.com";
        await _fixture.RegisterUserAsync(email);
        var creatorId = await _fixture.GetUserIdAsync(email);
        var trackId = await _fixture.SeedTrackAsync(creatorId, "Anon Play Beat");

        var client = _fixture.CreateClient(); // anonymous — no Authorization header

        var first = await client.PostAsJsonAsync("/stream/start", new { trackId = trackId.ToString() });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstData = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("started", firstData.GetProperty("status").GetString());

        // Repeat fire from the same client/IP within the hour must not create a 2nd play.
        var second = await client.PostAsJsonAsync("/stream/start", new { trackId = trackId.ToString() });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondData = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("already_counted", secondData.GetProperty("status").GetString());

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var sessions = await db.StreamSessions.Where(s => s.TrackId == trackId).ToListAsync();
        Assert.Single(sessions);
        Assert.Null(sessions[0].UserId); // attributed to no user (anonymous)
    }
}
