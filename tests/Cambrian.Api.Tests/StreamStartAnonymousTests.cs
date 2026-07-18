using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests;

/// <summary>
/// Anonymous playback may create a pending session, but start alone is never a play.
/// A stable anonymous session identifier deduplicates repeat starts without persisting
/// the raw identifier.
/// </summary>
public sealed class StreamStartAnonymousTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public StreamStartAnonymousTests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task StreamStart_LoggedOut_CreatesOnePendingSession_AndCountsNothing()
    {
        var email = $"anonplay-{Guid.NewGuid():N}@cambrian.com";
        await _fixture.RegisterUserAsync(email);
        var creatorId = await _fixture.GetUserIdAsync(email);
        var trackId = await _fixture.SeedTrackAsync(creatorId, "Anon Play Beat");

        var client = _fixture.CreateClient(); // anonymous — no Authorization header
        client.DefaultRequestHeaders.Add("X-Cambrian-Anonymous-Session", "anonymous-start-contract");

        var first = await client.PostAsJsonAsync("/stream/start", new { trackId = trackId.ToString() });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstData = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("started", firstData.GetProperty("status").GetString());

        // Repeat fire from the same anonymous identity must reuse the pending session.
        var second = await client.PostAsJsonAsync("/stream/start", new { trackId = trackId.ToString() });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondData = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("already_started", secondData.GetProperty("status").GetString());
        Assert.Equal(firstData.GetProperty("streamId").GetString(), secondData.GetProperty("streamId").GetString());

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var sessions = await db.StreamSessions.Where(s => s.TrackId == trackId).ToListAsync();
        Assert.Single(sessions);
        Assert.Null(sessions[0].UserId); // attributed to no user (anonymous)
        Assert.Equal("pending", sessions[0].QualificationStatus);
        Assert.Equal(64, sessions[0].ListenerKeyHash?.Length);
        Assert.DoesNotContain("anonymous-start-contract", sessions[0].ListenerKeyHash!, StringComparison.Ordinal);
        Assert.False(await db.QualifiedPlayEvents.AnyAsync(e => e.TrackId == trackId));
        Assert.False(await db.TrackStats.AnyAsync(s => s.TrackId == trackId));
    }
}
