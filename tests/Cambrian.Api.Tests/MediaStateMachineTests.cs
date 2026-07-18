using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Cambrian.Persistence.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Api.Tests;

public sealed class MediaStateMachineTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private CambrianDbContext _db = null!;
    private MediaStateMachine _service = null!;
    private Guid _trackId;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new CambrianDbContext(new DbContextOptionsBuilder<CambrianDbContext>()
            .UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        var user = new ApplicationUser { Id = "creator-1", UserName = "creator", NormalizedUserName = "CREATOR" };
        _db.Users.Add(user);
        _trackId = Guid.NewGuid();
        _db.Tracks.Add(new Track
        {
            Id = _trackId,
            CambrianTrackId = $"CAMB-TRK-{_trackId.ToString("N")[..8].ToUpperInvariant()}",
            Title = "Media State Test",
            CreatorId = user.Id,
            AudioUrl = "tracks/creator/test.mp3",
            Visibility = "hidden",
        });
        await _db.SaveChangesAsync();
        _service = new MediaStateMachine(_db, TimeProvider.System);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task LegacyBackfillNeverPromotesUnverifiedObjectToReady()
    {
        var media = await _service.InitializeLegacyAsync(_trackId, "tracks/creator/test.mp3");

        Assert.Equal(TrackMediaStates.Uploaded, media.State);
        Assert.Null(media.ValidatedAtUtc);
    }

    [Fact]
    public async Task AbsoluteLegacyLocationIsFailedForOperatorReview()
    {
        var media = await _service.InitializeLegacyAsync(_trackId, "https://bucket.example/tracks/test.mp3?signature=secret");

        Assert.Equal(TrackMediaStates.Failed, media.State);
        Assert.Equal("legacy_location_unrecognized", media.FailureCode);
        Assert.Null(media.ObjectKey);
    }

    [Fact]
    public async Task ValidTransitionChainReachesReadyAndRefreshesConcurrencyToken()
    {
        var uploaded = await _service.InitializeLegacyAsync(_trackId, "tracks/creator/test.mp3");
        var initialToken = uploaded.ConcurrencyToken;
        var validating = await _service.TransitionAsync(
            _trackId, initialToken, TrackMediaStates.Validating, new());
        var ready = await _service.TransitionAsync(
            _trackId,
            validating.ConcurrencyToken,
            TrackMediaStates.Ready,
            new(ValidatedAtUtc: DateTime.UtcNow, SizeBytes: 123, ContentType: "audio/mpeg"));

        Assert.Equal(TrackMediaStates.Ready, ready.State);
        Assert.NotEqual(initialToken, ready.ConcurrencyToken);
    }

    [Fact]
    public async Task InvalidAndStaleTransitionsAreRejected()
    {
        var uploaded = await _service.InitializeLegacyAsync(_trackId, "tracks/creator/test.mp3");
        var staleToken = uploaded.ConcurrencyToken;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.TransitionAsync(
            _trackId, staleToken, TrackMediaStates.Ready, new()));

        await _service.TransitionAsync(
            _trackId, staleToken, TrackMediaStates.Validating, new());
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => _service.TransitionAsync(
            _trackId, staleToken, TrackMediaStates.Failed, new()));
    }

    [Theory]
    [InlineData(TrackMediaStates.Draft)]
    [InlineData(TrackMediaStates.Uploading)]
    [InlineData(TrackMediaStates.Uploaded)]
    [InlineData(TrackMediaStates.Processing)]
    [InlineData(TrackMediaStates.Validating)]
    [InlineData(TrackMediaStates.Ready)]
    [InlineData(TrackMediaStates.Failed)]
    [InlineData(TrackMediaStates.Quarantined)]
    public async Task DeletedIsTerminalAndRejectsEveryExit(string targetState)
    {
        var uploaded = await _service.InitializeLegacyAsync(_trackId, "tracks/creator/test.mp3");
        var deleted = await _service.TransitionAsync(
            _trackId, uploaded.ConcurrencyToken, TrackMediaStates.Deleted, new());

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.TransitionAsync(
            _trackId, deleted.ConcurrencyToken, targetState, new()));
    }

    [Fact]
    public async Task QuarantinedCannotGoStraightToReadyButCanReenterValidation()
    {
        var uploaded = await _service.InitializeLegacyAsync(_trackId, "tracks/creator/test.mp3");
        var quarantined = await _service.TransitionAsync(
            _trackId, uploaded.ConcurrencyToken, TrackMediaStates.Quarantined, new());

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.TransitionAsync(
            _trackId, quarantined.ConcurrencyToken, TrackMediaStates.Ready, new()));

        // A rejected transition must not consume the concurrency token; the only
        // legal exits from Quarantined are re-validation and deletion.
        var validating = await _service.TransitionAsync(
            _trackId, quarantined.ConcurrencyToken, TrackMediaStates.Validating, new());
        Assert.Equal(TrackMediaStates.Validating, validating.State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyLegacyLocationInitializesAsDraft(string? location)
    {
        var media = await _service.InitializeLegacyAsync(_trackId, location);

        Assert.Equal(TrackMediaStates.Draft, media.State);
        Assert.Null(media.ObjectKey);
        Assert.Null(media.FailureCode);
    }

    [Fact]
    public async Task UploadsPrefixedLegacyLocationIsNormalizedToStableKey()
    {
        var media = await _service.InitializeLegacyAsync(_trackId, "/uploads/tracks/x.mp3");

        Assert.Equal(TrackMediaStates.Uploaded, media.State);
        Assert.Equal("tracks/x.mp3", media.ObjectKey);
        Assert.Null(media.ValidatedAtUtc);
    }

    [Theory]
    [InlineData("https://old.example/x.mp3?sig=1")]
    [InlineData("tracks/../x.mp3")]
    [InlineData("/stream/2e9c64f1-56cf-4d9e-9c3a-1f4b6a7d8e90/audio")]
    public async Task HazardousLegacyLocationsAreFailedForOperatorReview(string location)
    {
        var media = await _service.InitializeLegacyAsync(_trackId, location);

        Assert.Equal(TrackMediaStates.Failed, media.State);
        Assert.Equal("legacy_location_unrecognized", media.FailureCode);
        Assert.Null(media.ObjectKey);
    }

    [Fact]
    public async Task OverlongLegacyKeyIsFailedForOperatorReview()
    {
        var media = await _service.InitializeLegacyAsync(_trackId, "tracks/" + new string('a', 1_100));

        Assert.Equal(TrackMediaStates.Failed, media.State);
        Assert.Equal("legacy_location_unrecognized", media.FailureCode);
        Assert.Null(media.ObjectKey);
    }

    [Fact]
    public async Task RefreshValidationUpdatesMetadataAndRotatesTokenWithoutLeavingReady()
    {
        var ready = await AdvanceToReadyAsync();
        var priorToken = ready.ConcurrencyToken;
        var stateChangedAtUtc = ready.StateChangedAtUtc;
        var refreshedAtUtc = DateTime.UtcNow;

        var refreshed = await _service.RefreshValidationAsync(
            _trackId,
            priorToken,
            new(ValidatedAtUtc: refreshedAtUtc, SizeBytes: 456, ChecksumSha256: new string('b', 64)));

        Assert.Equal(TrackMediaStates.Ready, refreshed.State);
        Assert.Equal(refreshedAtUtc, refreshed.ValidatedAtUtc);
        Assert.Equal(456L, refreshed.SizeBytes);
        Assert.Equal(new string('b', 64), refreshed.ChecksumSha256);
        Assert.NotEqual(priorToken, refreshed.ConcurrencyToken);
        // A validation refresh is not a state change — StateChangedAtUtc must not move.
        Assert.Equal(stateChangedAtUtc, refreshed.StateChangedAtUtc);
    }

    [Fact]
    public async Task RefreshValidationRejectsStaleConcurrencyToken()
    {
        await AdvanceToReadyAsync();

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => _service.RefreshValidationAsync(
            _trackId, Guid.NewGuid(), new(ValidatedAtUtc: DateTime.UtcNow)));
    }

    [Fact]
    public async Task RefreshValidationIsOnlyAllowedFromReady()
    {
        var uploaded = await _service.InitializeLegacyAsync(_trackId, "tracks/creator/test.mp3");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RefreshValidationAsync(
            _trackId, uploaded.ConcurrencyToken, new(ValidatedAtUtc: DateTime.UtcNow)));
    }

    private async Task<TrackMedia> AdvanceToReadyAsync()
    {
        var uploaded = await _service.InitializeLegacyAsync(_trackId, "tracks/creator/test.mp3");
        var validating = await _service.TransitionAsync(
            _trackId, uploaded.ConcurrencyToken, TrackMediaStates.Validating, new());
        return await _service.TransitionAsync(
            _trackId,
            validating.ConcurrencyToken,
            TrackMediaStates.Ready,
            new(ValidatedAtUtc: DateTime.UtcNow.AddDays(-1), SizeBytes: 123, ContentType: "audio/mpeg"));
    }
}
