using Cambrian.Application.AI.Discovery.Ranking;
using Cambrian.Application.AI.Discovery.Services;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Cambrian.Api.Tests.Unit.Application;

/// <summary>
/// The AI single-track preview/details must report <c>Available</c> from a real object-existence
/// check, not just a non-empty key — so a track whose audio object is missing (the rehydration
/// gap) is never advertised as playable.
/// </summary>
public sealed class TrackDiscoveryPreviewAvailabilityTests
{
    private readonly ITrackRepository _tracks = Substitute.For<ITrackRepository>();
    private readonly IObjectStorage _storage = Substitute.For<IObjectStorage>();
    private readonly TrackDiscoveryService _service;

    public TrackDiscoveryPreviewAvailabilityTests()
    {
        var users = Substitute.For<UserManager<ApplicationUser>>(
            Substitute.For<IUserStore<ApplicationUser>>(), null, null, null, null, null, null, null, null);

        _service = new TrackDiscoveryService(
            _tracks, users,
            Substitute.For<ICreatorIdentityRepository>(),
            Substitute.For<ICreatorProfileRepository>(),
            Substitute.For<ITrackRankingService>(),
            _storage,
            Substitute.For<ILogger<TrackDiscoveryService>>());
    }

    private static Track PublicTrack(string? audioUrl) => new()
    {
        Id = Guid.NewGuid(),
        CambrianTrackId = "CAMB-TRK-AVAIL01",
        Title = "Probe",
        AudioUrl = audioUrl,
        Visibility = "public",
        CreatorId = "creator-1",
    };

    [Fact]
    public async Task Preview_ObjectExists_ReportsAvailableTrue()
    {
        var track = PublicTrack("tracks/real.mp3");
        _tracks.GetByCambrianTrackIdAsync("CAMB-TRK-AVAIL01").Returns(track);
        _storage.OpenReadAsync("tracks/real.mp3").Returns(new StorageFile
        {
            Stream = new MemoryStream(new byte[] { 1 }),
            ContentType = "audio/mpeg",
            Length = 1,
        });

        var preview = await _service.GetPreviewAsync("CAMB-TRK-AVAIL01");

        Assert.NotNull(preview);
        Assert.True(preview!.Available);
    }

    [Fact]
    public async Task Preview_ObjectMissing_ReportsAvailableFalse_EvenWithKeySet()
    {
        var track = PublicTrack("tracks/missing.mp3");
        _tracks.GetByCambrianTrackIdAsync("CAMB-TRK-AVAIL01").Returns(track);
        _storage.OpenReadAsync("tracks/missing.mp3").Returns((StorageFile?)null);

        var preview = await _service.GetPreviewAsync("CAMB-TRK-AVAIL01");

        Assert.NotNull(preview);
        Assert.False(preview!.Available); // key present, object gone → not playable
    }
}
