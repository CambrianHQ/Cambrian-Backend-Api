using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Cambrian.Persistence.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Unit-style suite for the publish-time media readiness gate
/// (<see cref="MediaReadinessService.EnsureReadyAsync"/>): promotable states
/// (Uploaded/Failed) run synchronous validation through the REAL
/// <see cref="MediaStateMachine"/> and land in Ready, Failed, or Quarantined;
/// a validation-dependency outage parks the row in the retryable Failed state
/// (self-healing, never a deadlock) and is never treated as evidence against
/// the media; every non-promotable state fails closed without validating.
/// </summary>
public sealed class MediaReadinessServiceTests : IAsyncLifetime
{
    private const string ObjectKey = "tracks/creator/test.mp3";
    private static readonly string ValidChecksum = new('a', 64);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private CambrianDbContext _db = null!;
    private IMediaValidationService _validation = null!;
    private MediaReadinessService _service = null!;
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
            Title = "Media Readiness Test",
            CreatorId = user.Id,
            AudioUrl = ObjectKey,
            Visibility = "hidden",
        });
        await _db.SaveChangesAsync();

        _validation = Substitute.For<IMediaValidationService>();
        _service = new MediaReadinessService(
            _db, _validation, new MediaStateMachine(_db, TimeProvider.System), TimeProvider.System);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    // ───────────────────────── helpers ─────────────────────────

    private async Task SeedMediaAsync(
        string state,
        string? objectKey = ObjectKey,
        string? failureCode = null,
        string? failureDetail = null)
    {
        _db.TrackMedia.Add(new TrackMedia
        {
            TrackId = _trackId,
            ObjectKey = objectKey,
            State = state,
            FailureCode = failureCode,
            FailureDetail = failureDetail,
            StateChangedAtUtc = DateTime.UtcNow,
            ConcurrencyToken = Guid.NewGuid(),
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private Task<TrackMedia> ReadMediaAsync() =>
        _db.TrackMedia.AsNoTracking().SingleAsync(x => x.TrackId == _trackId);

    private void SetValidationResult(MediaValidationResult result) =>
        _validation.ValidateAsync(Arg.Any<MediaValidationRequest>(), Arg.Any<CancellationToken>())
            .Returns(result);

    private static MediaValidationResult Valid() =>
        new(true, null, null, false, 4, "audio/mpeg", ValidChecksum, 30_000, "test-v1");

    private Task AssertValidationNeverCalledAsync() =>
        _validation.DidNotReceive().ValidateAsync(
            Arg.Any<MediaValidationRequest>(), Arg.Any<CancellationToken>());

    // ───────────────────────── 1. happy path ─────────────────────────

    [Fact]
    public async Task UploadedMediaWithValidObjectIsPromotedToReadyWithStampedMetadata()
    {
        await SeedMediaAsync(TrackMediaStates.Uploaded);
        SetValidationResult(Valid());

        var result = await _service.EnsureReadyAsync(_trackId);

        Assert.True(result.IsReady);
        Assert.Null(result.FailureCode);
        Assert.Equal(TrackMediaStates.Ready, result.MediaState);

        var row = await ReadMediaAsync();
        Assert.Equal(TrackMediaStates.Ready, row.State);
        Assert.NotNull(row.ValidatedAtUtc);
        Assert.Equal(ValidChecksum, row.ChecksumSha256);
        Assert.Equal(30_000, row.DurationMilliseconds);
        Assert.Equal(4, row.SizeBytes);
        Assert.Equal("audio/mpeg", row.ContentType);
        Assert.Equal("test-v1", row.ValidationVersion);
        Assert.Null(row.FailureCode);
    }

    // ───────────────────────── 2. genuine validation failures ─────────────────────────

    [Theory]
    [InlineData("media_parse_failed", TrackMediaStates.Quarantined)]
    [InlineData("media_object_missing", TrackMediaStates.Failed)]
    public async Task UploadedMediaFailingValidationLandsInTheMatchingFailureState(
        string validationCode, string expectedState)
    {
        await SeedMediaAsync(TrackMediaStates.Uploaded);
        SetValidationResult(MediaValidationResult.Failure(
            validationCode, "Deterministic validation failure.", "test-v1"));

        var result = await _service.EnsureReadyAsync(_trackId);

        Assert.False(result.IsReady);
        Assert.Equal(validationCode, result.FailureCode);
        Assert.Equal(expectedState, result.MediaState);

        var row = await ReadMediaAsync();
        Assert.Equal(expectedState, row.State);
        Assert.Equal(validationCode, row.FailureCode);
        Assert.Null(row.ValidatedAtUtc);
    }

    // ───────────────────────── 3. outage parks in Failed, then self-heals ─────────────────────────

    [Fact]
    public async Task StorageOutageParksUploadedMediaInFailedAndTheNextAttemptSelfHeals()
    {
        await SeedMediaAsync(TrackMediaStates.Uploaded);
        SetValidationResult(MediaValidationResult.Failure(
            "storage_unavailable", "Storage probe timed out.", "test-v1", dependencyUnavailable: true));

        var outage = await _service.EnsureReadyAsync(_trackId);

        Assert.False(outage.IsReady);
        Assert.Equal("storage_unavailable", outage.FailureCode);
        Assert.Equal(TrackMediaStates.Failed, outage.MediaState);
        var parked = await ReadMediaAsync();
        Assert.Equal(TrackMediaStates.Failed, parked.State);
        Assert.Equal("storage_unavailable", parked.FailureCode);
        Assert.Null(parked.ValidatedAtUtc);

        // Storage comes back: the same entry point promotes the parked row to
        // Ready (Failed -> Validating -> Ready) with no operator involvement.
        SetValidationResult(Valid());
        var healed = await _service.EnsureReadyAsync(_trackId);

        Assert.True(healed.IsReady);
        Assert.Equal(TrackMediaStates.Ready, healed.MediaState);
        var ready = await ReadMediaAsync();
        Assert.Equal(TrackMediaStates.Ready, ready.State);
        Assert.Null(ready.FailureCode);
        Assert.NotNull(ready.ValidatedAtUtc);
    }

    // ───────────────────────── 4. outage preserves the original diagnosis ─────────────────────────

    [Fact]
    public async Task StorageOutageRestoresTheOriginalFailureInfoOnFailedOriginMedia()
    {
        await SeedMediaAsync(
            TrackMediaStates.Failed,
            failureCode: "decode_probe_failed",
            failureDetail: "Probe could not decode the audio stream.");
        SetValidationResult(MediaValidationResult.Failure(
            "storage_unavailable", "Storage probe timed out.", "test-v1", dependencyUnavailable: true));

        var result = await _service.EnsureReadyAsync(_trackId);

        // The caller sees the outage; the row keeps its ORIGINAL diagnosis —
        // an outage is not evidence for or against previously failed media.
        Assert.False(result.IsReady);
        Assert.Equal("storage_unavailable", result.FailureCode);
        Assert.Equal(TrackMediaStates.Failed, result.MediaState);

        var row = await ReadMediaAsync();
        Assert.Equal(TrackMediaStates.Failed, row.State);
        Assert.Equal("decode_probe_failed", row.FailureCode);
        Assert.Equal("Probe could not decode the audio stream.", row.FailureDetail);
    }

    // ───────────────────────── 5. Ready short-circuits ─────────────────────────

    [Fact]
    public async Task ReadyMediaShortCircuitsWithoutConsultingValidation()
    {
        await SeedMediaAsync(TrackMediaStates.Ready);
        var before = await ReadMediaAsync();

        var result = await _service.EnsureReadyAsync(_trackId);

        Assert.True(result.IsReady);
        Assert.Equal(TrackMediaStates.Ready, result.MediaState);
        var after = await ReadMediaAsync();
        Assert.Equal(before.ConcurrencyToken, after.ConcurrencyToken);
        await AssertValidationNeverCalledAsync();
    }

    // ───────────────────────── 6. non-promotable states fail closed ─────────────────────────

    [Fact]
    public async Task ValidatingMediaReportsInProgressWithoutWritesOrValidation()
    {
        await SeedMediaAsync(TrackMediaStates.Validating);
        var before = await ReadMediaAsync();

        var result = await _service.EnsureReadyAsync(_trackId);

        Assert.False(result.IsReady);
        Assert.Equal("media_validating", result.FailureCode);
        Assert.Equal(TrackMediaStates.Validating, result.MediaState);

        var after = await ReadMediaAsync();
        Assert.Equal(TrackMediaStates.Validating, after.State);
        Assert.Equal(before.ConcurrencyToken, after.ConcurrencyToken);
        Assert.Equal(before.StateChangedAtUtc, after.StateChangedAtUtc);
        await AssertValidationNeverCalledAsync();
    }

    [Theory]
    [InlineData(TrackMediaStates.Quarantined)]
    [InlineData(TrackMediaStates.Draft)]
    [InlineData(TrackMediaStates.Uploading)]
    [InlineData(TrackMediaStates.Processing)]
    [InlineData(TrackMediaStates.Deleted)]
    public async Task NonPromotableStatesAreNotReadyAndValidationIsNeverConsulted(string state)
    {
        await SeedMediaAsync(state, failureCode: state == TrackMediaStates.Quarantined ? "media_parse_failed" : null);
        var before = await ReadMediaAsync();

        var result = await _service.EnsureReadyAsync(_trackId);

        Assert.False(result.IsReady);
        Assert.Equal("track_not_ready", result.FailureCode);
        Assert.Equal(state, result.MediaState);

        var after = await ReadMediaAsync();
        Assert.Equal(state, after.State);
        Assert.Equal(before.ConcurrencyToken, after.ConcurrencyToken);
        await AssertValidationNeverCalledAsync();
    }

    [Fact]
    public async Task MissingRowOrBlankObjectKeyFailsClosedAsTrackNotReady()
    {
        // No TrackMedia row at all.
        var missing = await _service.EnsureReadyAsync(_trackId);
        Assert.False(missing.IsReady);
        Assert.Equal("track_not_ready", missing.FailureCode);
        Assert.Null(missing.MediaState);

        // A row without a usable storage key is equally unpublishable — even in
        // the otherwise promotable Uploaded state.
        await SeedMediaAsync(TrackMediaStates.Uploaded, objectKey: "   ");
        var blankKey = await _service.EnsureReadyAsync(_trackId);
        Assert.False(blankKey.IsReady);
        Assert.Equal("track_not_ready", blankKey.FailureCode);
        Assert.Equal(TrackMediaStates.Uploaded, (await ReadMediaAsync()).State);
        await AssertValidationNeverCalledAsync();
    }
}
