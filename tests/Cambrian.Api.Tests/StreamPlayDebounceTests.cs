using Cambrian.Api.Tests.Fixtures;
using Cambrian.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cambrian.Api.Tests;

/// <summary>
/// Play counts must not be trivially inflatable: rapid repeat starts by the same user on the
/// same track collapse into a single play, while distinct tracks and distinct anonymous clients
/// still count. Debouncing is a durable, database-backed idempotency key (see StreamRepository),
/// not an in-process check — see StreamStartAnonymousTests for the same-anonymous-client-is-
/// debounced case, exercised through the real HTTP endpoint (client IP comes from HttpContext there).
/// </summary>
public sealed class StreamPlayDebounceTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public StreamPlayDebounceTests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task StartAsync_RapidRepeatSameUserTrack_DebouncesToOnePlay()
    {
        var email = $"debounce-{Guid.NewGuid():N}@cambrian.com";
        await _fixture.RegisterUserAsync(email);
        var creatorId = await _fixture.GetUserIdAsync(email);
        var trackId = await _fixture.SeedTrackAsync(creatorId, "Debounce Beat");
        var otherTrackId = await _fixture.SeedTrackAsync(creatorId, "Other Beat");

        using var scope = _fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IStreamRepository>();
        var playCounts = scope.ServiceProvider.GetRequiredService<IPlayCountService>();
        const string userId = "debounce-user-1";

        var (first, firstIsNew) = await repo.StartAsync(trackId, userId, null);
        var (second, secondIsNew) = await repo.StartAsync(trackId, userId, null);

        Assert.Equal(first.Id, second.Id); // collapsed into one play
        Assert.True(firstIsNew);
        Assert.False(secondIsNew);

        var (other, otherIsNew) = await repo.StartAsync(otherTrackId, userId, null);
        Assert.NotEqual(first.Id, other.Id); // a different track is a distinct play
        Assert.True(otherIsNew);

        var counts = await playCounts.GetTrackPlayCountsAsync(new[] { trackId });
        Assert.Equal(1L, counts[trackId]);
    }

    [Fact]
    public async Task StartAsync_DistinctAnonymousClients_AreNotDebounced()
    {
        var email = $"anon-debounce-{Guid.NewGuid():N}@cambrian.com";
        await _fixture.RegisterUserAsync(email);
        var creatorId = await _fixture.GetUserIdAsync(email);
        var trackId = await _fixture.SeedTrackAsync(creatorId, "Anon Debounce Beat");

        using var scope = _fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IStreamRepository>();

        var (a, aIsNew) = await repo.StartAsync(trackId, null, "203.0.113.10");
        var (b, bIsNew) = await repo.StartAsync(trackId, null, "203.0.113.20");

        Assert.NotEqual(a.Id, b.Id); // distinct anonymous clients → distinct plays
        Assert.True(aIsNew);
        Assert.True(bIsNew);
    }
}
