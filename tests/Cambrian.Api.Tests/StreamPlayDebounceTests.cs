using Cambrian.Api.Tests.Fixtures;
using Cambrian.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cambrian.Api.Tests;

/// <summary>
/// Play counts must not be trivially inflatable: rapid repeat starts by the same user on the
/// same track collapse into a single play, while distinct tracks and anonymous plays still count.
/// </summary>
public sealed class StreamPlayDebounceTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public StreamPlayDebounceTests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task StartAsync_RapidRepeatSameUserTrack_DebouncesToOnePlay()
    {
        using var scope = _fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IStreamRepository>();
        var trackId = Guid.NewGuid();
        const string userId = "debounce-user-1";

        var first = await repo.StartAsync(trackId, userId);
        var second = await repo.StartAsync(trackId, userId);

        Assert.Equal(first.Id, second.Id); // collapsed into one play

        var other = await repo.StartAsync(Guid.NewGuid(), userId);
        Assert.NotEqual(first.Id, other.Id); // a different track is a distinct play

        var counts = await repo.GetPlayCountsByTrackIdsAsync(new[] { trackId });
        Assert.Equal(1, counts[trackId]);
    }

    [Fact]
    public async Task StartAsync_AnonymousPlays_AreNotDebounced()
    {
        using var scope = _fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IStreamRepository>();
        var trackId = Guid.NewGuid();

        var a = await repo.StartAsync(trackId, null);
        var b = await repo.StartAsync(trackId, null);

        Assert.NotEqual(a.Id, b.Id); // anonymous can't be attributed → not collapsed
    }
}
