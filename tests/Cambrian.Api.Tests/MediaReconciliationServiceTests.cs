using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Cambrian.Persistence.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

public sealed class MediaReconciliationServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private CambrianDbContext _db = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new CambrianDbContext(new DbContextOptionsBuilder<CambrianDbContext>()
            .UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        _db.Users.Add(new ApplicationUser { Id = "creator-1", UserName = "creator", NormalizedUserName = "CREATOR" });
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task RunDetectsDriftPromotesVerifiedCandidateAndSafelyDemotesMissingReadyObject()
    {
        var missingId = AddTrack("Missing Ready", "public", "tracks/missing.mp3", TrackMediaStates.Ready, 100, 30_000);
        var candidateId = AddTrack("Candidate", "hidden", "tracks/shared.mp3", TrackMediaStates.Uploaded, null, null);
        var duplicateId = AddTrack("Duplicate", "hidden", "tracks/shared.mp3", TrackMediaStates.Uploaded, null, null);
        var legacyId = Guid.NewGuid();
        _db.Tracks.Add(new Track
        {
            Id = legacyId,
            CambrianTrackId = CambrianId(legacyId),
            Title = "Legacy",
            CreatorId = "creator-1",
            Visibility = "public",
            AudioUrl = "https://legacy.example/bucket/audio.mp3?token=old",
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var storage = Substitute.For<IObjectStorage>();
        storage.ListAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Objects(
            new("tracks/shared.mp3", 256, "audio/mpeg", "etag", DateTime.UtcNow),
            new("tracks/orphan.mp3", 128, "audio/mpeg", "etag", DateTime.UtcNow)));
        // The demotion path HEAD-confirms absence before acting on a listing miss.
        storage.GetMetadataAsync("tracks/missing.mp3", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<StorageObjectMetadata?>(null));
        var validation = Substitute.For<IMediaValidationService>();
        validation.ValidateAsync(Arg.Any<MediaValidationRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<MediaValidationRequest>();
                return new MediaValidationResult(
                    true, null, null, false, 256, "audio/mpeg", new string('a', 64),
                    30_000, "media-v1");
            });
        var stateMachine = new MediaStateMachine(_db, TimeProvider.System);
        var service = new MediaReconciliationService(
            _db, storage, validation, stateMachine, TimeProvider.System,
            Substitute.For<ILogger<MediaReconciliationService>>());

        var summary = await service.RunAsync(remediate: true);
        var report = await service.GetRunAsync(summary.RunId);

        Assert.NotNull(report);
        var types = report!.Findings.Select(x => x.FindingType).ToHashSet();
        Assert.Contains("database_row_without_object", types);
        Assert.Contains("object_without_database_row", types);
        Assert.Contains("duplicate_object_key", types);
        Assert.Contains("legacy_bucket_domain_reference", types);
        Assert.Contains("published_track_not_ready", types);
        Assert.Equal(TrackMediaStates.Failed, (await _db.TrackMedia.FindAsync(missingId))!.State);
        Assert.Equal(TrackMediaStates.Ready, (await _db.TrackMedia.FindAsync(candidateId))!.State);
        Assert.Equal(TrackMediaStates.Ready, (await _db.TrackMedia.FindAsync(duplicateId))!.State);
        Assert.Equal(TrackMediaStates.Failed, (await _db.TrackMedia.FindAsync(legacyId))!.State);
    }

    [Fact]
    public async Task DetectionOnlyRunFindsDriftWithoutChangingAnyMediaState()
    {
        var missingId = AddTrack("Missing Ready", "public", "tracks/missing.mp3", TrackMediaStates.Ready, 100, 30_000);
        var candidateId = AddTrack("Candidate", "hidden", "tracks/shared.mp3", TrackMediaStates.Uploaded, null, null);
        var duplicateId = AddTrack("Duplicate", "hidden", "tracks/shared.mp3", TrackMediaStates.Uploaded, null, null);
        var legacyId = Guid.NewGuid();
        _db.Tracks.Add(new Track
        {
            Id = legacyId,
            CambrianTrackId = CambrianId(legacyId),
            Title = "Legacy",
            CreatorId = "creator-1",
            Visibility = "public",
            AudioUrl = "https://legacy.example/bucket/audio.mp3?token=old",
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var storage = Substitute.For<IObjectStorage>();
        storage.ListAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Objects(
            new("tracks/shared.mp3", 256, "audio/mpeg", "etag", DateTime.UtcNow),
            new("tracks/orphan.mp3", 128, "audio/mpeg", "etag", DateTime.UtcNow)));
        storage.GetMetadataAsync("tracks/missing.mp3", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<StorageObjectMetadata?>(null));
        var validation = Substitute.For<IMediaValidationService>();
        var service = CreateService(storage, validation);

        var summary = await service.RunAsync(remediate: false);
        var report = await service.GetRunAsync(summary.RunId);

        Assert.Equal("completed", summary.Status);
        var types = report!.Findings.Select(x => x.FindingType).ToHashSet();
        Assert.Contains("database_row_without_object", types);
        Assert.Contains("object_without_database_row", types);
        Assert.Contains("duplicate_object_key", types);
        Assert.Contains("legacy_bucket_domain_reference", types);
        Assert.Contains("published_track_not_ready", types);
        // Pure detection: no promotion, no demotion, no validation probes.
        Assert.Equal(TrackMediaStates.Ready, (await _db.TrackMedia.FindAsync(missingId))!.State);
        Assert.Equal(TrackMediaStates.Uploaded, (await _db.TrackMedia.FindAsync(candidateId))!.State);
        Assert.Equal(TrackMediaStates.Uploaded, (await _db.TrackMedia.FindAsync(duplicateId))!.State);
        // The legacy bootstrap row is created (not transitioned) by
        // InitializeLegacyAsync and lands in Failed because the URL is opaque.
        Assert.Equal(TrackMediaStates.Failed, (await _db.TrackMedia.FindAsync(legacyId))!.State);
        await validation.DidNotReceive().ValidateAsync(Arg.Any<MediaValidationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemediatingRunQuarantinesUploadedCandidateThatFailsDecodeProbe()
    {
        var candidateId = AddTrack("Broken Candidate", "hidden", "tracks/broken.mp3", TrackMediaStates.Uploaded, null, null);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var storage = Substitute.For<IObjectStorage>();
        storage.ListAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Objects(
            new StorageObjectMetadata("tracks/broken.mp3", 256, "audio/mpeg", "etag", DateTime.UtcNow)));
        var validation = Substitute.For<IMediaValidationService>();
        validation.ValidateAsync(Arg.Any<MediaValidationRequest>(), Arg.Any<CancellationToken>())
            .Returns(MediaValidationResult.Failure(
                "decode_probe_failed", "The audio payload could not be decoded.", "media-v1"));
        var service = CreateService(storage, validation);

        var summary = await service.RunAsync(remediate: true);
        var report = await service.GetRunAsync(summary.RunId);

        var media = (await _db.TrackMedia.FindAsync(candidateId))!;
        // Decode/parse/checksum failures map to Quarantined — never Ready.
        Assert.Equal(TrackMediaStates.Quarantined, media.State);
        Assert.Equal("decode_probe_failed", media.FailureCode);
        Assert.Contains(report!.Findings, x =>
            x.TrackId == candidateId && x.FindingType == "decode_probe_failed" && x.Severity == "error");
    }

    [Fact]
    public async Task DependencyOutageLeavesStatesUndemotedAndRecordsWarningFindings()
    {
        var readyId = AddTrack("Ready Track", "public", "tracks/ready.mp3", TrackMediaStates.Ready, 256, 30_000);
        var candidateId = AddTrack("Candidate", "hidden", "tracks/candidate.mp3", TrackMediaStates.Uploaded, 256, 30_000);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var storage = Substitute.For<IObjectStorage>();
        storage.ListAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Objects(
            new("tracks/ready.mp3", 256, "audio/mpeg", "etag", DateTime.UtcNow),
            new("tracks/candidate.mp3", 256, "audio/mpeg", "etag", DateTime.UtcNow)));
        var validation = Substitute.For<IMediaValidationService>();
        validation.ValidateAsync(Arg.Any<MediaValidationRequest>(), Arg.Any<CancellationToken>())
            .Returns(MediaValidationResult.Failure(
                "production_probe_unavailable", "The production probe endpoint timed out.", "media-v1",
                dependencyUnavailable: true));
        var service = CreateService(storage, validation);

        var summary = await service.RunAsync(remediate: true);
        var report = await service.GetRunAsync(summary.RunId);

        Assert.Equal("completed", summary.Status);
        // The published Ready row must stay Ready; the candidate parks in
        // Validating (the pre-validation transition) — neither is demoted nor
        // promoted while the dependency is down.
        Assert.Equal(TrackMediaStates.Ready, (await _db.TrackMedia.FindAsync(readyId))!.State);
        Assert.Equal(TrackMediaStates.Validating, (await _db.TrackMedia.FindAsync(candidateId))!.State);
        var outages = report!.Findings.Where(x => x.FindingType == "validation_dependency_unavailable").ToList();
        Assert.Equal(2, outages.Count);
        Assert.All(outages, x =>
        {
            Assert.Equal("warning", x.Severity);
            Assert.Equal("none", x.Resolution);
        });
        // A transient outage is not an unresolved published-track failure.
        Assert.Equal(0, summary.UnresolvedPublishedTrackFailures);
    }

    [Fact]
    public async Task RemediatingRunNeverDeletesStorageObjects()
    {
        var missingId = AddTrack("Missing Ready", "public", "tracks/missing.mp3", TrackMediaStates.Ready, 100, 30_000);
        var failingId = AddTrack("Failing Candidate", "hidden", "tracks/failing.mp3", TrackMediaStates.Uploaded, null, null);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var storage = Substitute.For<IObjectStorage>();
        storage.ListAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Objects(
            new("tracks/failing.mp3", 256, "audio/mpeg", "etag", DateTime.UtcNow),
            new("tracks/orphan.mp3", 128, "audio/mpeg", "etag", DateTime.UtcNow)));
        storage.GetMetadataAsync("tracks/missing.mp3", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<StorageObjectMetadata?>(null));
        var validation = Substitute.For<IMediaValidationService>();
        validation.ValidateAsync(Arg.Any<MediaValidationRequest>(), Arg.Any<CancellationToken>())
            .Returns(MediaValidationResult.Failure(
                "decode_probe_failed", "The audio payload could not be decoded.", "media-v1"));
        var service = CreateService(storage, validation);

        var summary = await service.RunAsync(remediate: true);

        Assert.Equal("completed", summary.Status);
        // Both demotion paths ran (missing object + failed probe) and an orphan
        // object was reported, yet reconciliation must never destroy storage.
        Assert.Equal(TrackMediaStates.Failed, (await _db.TrackMedia.FindAsync(missingId))!.State);
        Assert.Equal(TrackMediaStates.Quarantined, (await _db.TrackMedia.FindAsync(failingId))!.State);
        await storage.DidNotReceive().DeleteAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task EmptyStorageListingAbortsTheRunWithoutDemotingReadyRows()
    {
        var readyId = AddTrack("Ready Track", "public", "tracks/ready.mp3", TrackMediaStates.Ready, 256, 30_000);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var storage = Substitute.For<IObjectStorage>();
        storage.ListAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Objects());
        var validation = Substitute.For<IMediaValidationService>();
        var service = CreateService(storage, validation);

        var summary = await service.RunAsync(remediate: true);

        Assert.Equal("failed", summary.Status);
        var run = await _db.MediaReconciliationRuns.SingleAsync(x => x.Id == summary.RunId);
        Assert.Equal("storage_listing_empty", run.FailureCode);
        Assert.Equal(TrackMediaStates.Ready, (await _db.TrackMedia.FindAsync(readyId))!.State);
        // The abort fires before per-track inspection, so no HEAD probes run.
        await storage.DidNotReceive().GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HeadConfirmRescuesKeysMissingFromAStaleListing()
    {
        var rescuedId = AddTrack("Rescued Ready", "public", "tracks/rescued.mp3", TrackMediaStates.Ready, 100, 30_000);
        AddTrack("Listed Track", "hidden", "tracks/listed.mp3", TrackMediaStates.Uploaded, 256, 30_000);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var storage = Substitute.For<IObjectStorage>();
        // The listing misses the rescued key (eventual consistency) but the
        // HEAD confirmation still finds a real object.
        storage.ListAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Objects(
            new StorageObjectMetadata("tracks/listed.mp3", 256, "audio/mpeg", "etag", DateTime.UtcNow)));
        storage.GetMetadataAsync("tracks/rescued.mp3", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<StorageObjectMetadata?>(
                new StorageObjectMetadata("tracks/rescued.mp3", 100, "audio/mpeg", "etag", DateTime.UtcNow)));
        var validation = Substitute.For<IMediaValidationService>();
        validation.ValidateAsync(Arg.Any<MediaValidationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MediaValidationResult(
                true, null, null, false, 100, "audio/mpeg", new string('a', 64), 30_000, "media-v1"));
        var service = CreateService(storage, validation);

        var summary = await service.RunAsync(remediate: true);
        var report = await service.GetRunAsync(summary.RunId);

        var media = (await _db.TrackMedia.FindAsync(rescuedId))!;
        Assert.Equal(TrackMediaStates.Ready, media.State);
        Assert.Null(media.FailureCode);
        Assert.DoesNotContain(report!.Findings, x => x.FindingType == "database_row_without_object");
    }

    [Fact]
    public async Task DetectionFlagsZeroByteSizeMismatchAndOnlyDefinitiveWrongMime()
    {
        var zeroId = AddTrack("Zero Byte", "hidden", "tracks/zero.mp3", TrackMediaStates.Uploaded, null, 30_000);
        var mismatchId = AddTrack("Size Mismatch", "hidden", "tracks/mismatch.mp3", TrackMediaStates.Uploaded, 100, 30_000);
        var wrongMimeId = AddTrack("Wrong Mime", "hidden", "tracks/wrong-mime.mp3", TrackMediaStates.Uploaded, 256, 30_000);
        var unknownMimeId = AddTrack("Unknown Mime", "hidden", "tracks/unknown-mime.mp3", TrackMediaStates.Uploaded, 256, 30_000);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var storage = Substitute.For<IObjectStorage>();
        storage.ListAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Objects(
            new("tracks/zero.mp3", 0, "audio/mpeg", "etag", DateTime.UtcNow),
            new("tracks/mismatch.mp3", 256, "audio/mpeg", "etag", DateTime.UtcNow),
            new("tracks/wrong-mime.mp3", 256, "image/png", "etag", DateTime.UtcNow),
            new("tracks/unknown-mime.mp3", 256, null, "etag", DateTime.UtcNow)));
        // Neither the listing nor HEAD can resolve a content type for the
        // unknown key; the validated row's audio/mpeg must win and no
        // wrong_mime_type finding may be raised on an unknown type.
        storage.GetMetadataAsync("tracks/unknown-mime.mp3", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<StorageObjectMetadata?>(
                new StorageObjectMetadata("tracks/unknown-mime.mp3", 256, null, "etag", DateTime.UtcNow)));
        var validation = Substitute.For<IMediaValidationService>();
        var service = CreateService(storage, validation);

        var summary = await service.RunAsync(remediate: false);
        var report = await service.GetRunAsync(summary.RunId);

        Assert.Contains(report!.Findings, x => x.TrackId == zeroId && x.FindingType == "zero_byte_object");
        Assert.Contains(report.Findings, x => x.TrackId == mismatchId && x.FindingType == "size_mismatch");
        Assert.Contains(report.Findings, x => x.TrackId == wrongMimeId && x.FindingType == "wrong_mime_type");
        Assert.DoesNotContain(report.Findings, x => x.TrackId == unknownMimeId && x.FindingType == "wrong_mime_type");
    }

    [Fact]
    public async Task FirstRunDetectsDuplicateKeysAcrossLegacyRowsInitializedMidRun()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        foreach (var id in new[] { firstId, secondId })
        {
            _db.Tracks.Add(new Track
            {
                Id = id,
                CambrianTrackId = CambrianId(id),
                Title = "Legacy Twin",
                CreatorId = "creator-1",
                Visibility = "hidden",
                AudioUrl = "tracks/legacy-shared.mp3",
            });
        }
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var storage = Substitute.For<IObjectStorage>();
        storage.ListAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Objects(
            new StorageObjectMetadata("tracks/legacy-shared.mp3", 256, "audio/mpeg", "etag", DateTime.UtcNow)));
        var validation = Substitute.For<IMediaValidationService>();
        var service = CreateService(storage, validation);

        var summary = await service.RunAsync(remediate: false);
        var report = await service.GetRunAsync(summary.RunId);

        // Neither track had a TrackMedia row before the run; the duplicate is
        // only visible through the rows InitializeLegacyAsync creates mid-run.
        var duplicates = report!.Findings.Where(x => x.FindingType == "duplicate_object_key").ToList();
        Assert.Equal(2, duplicates.Count);
        Assert.Equal(
            new[] { firstId, secondId }.OrderBy(x => x).ToArray(),
            duplicates.Select(x => x.TrackId!.Value).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task StorageListingFailureMarksTheRunFailedWithStorageUnavailable()
    {
        var readyId = AddTrack("Ready Track", "public", "tracks/ready.mp3", TrackMediaStates.Ready, 256, 30_000);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var storage = Substitute.For<IObjectStorage>();
        storage.ListAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(FailingListing(new HttpRequestException("R2 listing failed")));
        var validation = Substitute.For<IMediaValidationService>();
        var service = CreateService(storage, validation);

        var summary = await service.RunAsync(remediate: true);

        Assert.Equal("failed", summary.Status);
        var run = await _db.MediaReconciliationRuns.SingleAsync(x => x.Id == summary.RunId);
        Assert.Equal("storage_unavailable", run.FailureCode);
        Assert.Equal(TrackMediaStates.Ready, (await _db.TrackMedia.FindAsync(readyId))!.State);
    }

    private MediaReconciliationService CreateService(IObjectStorage storage, IMediaValidationService validation) =>
        new(_db, storage, validation, new MediaStateMachine(_db, TimeProvider.System), TimeProvider.System,
            Substitute.For<ILogger<MediaReconciliationService>>());

    private Guid AddTrack(
        string title,
        string visibility,
        string objectKey,
        string state,
        long? size,
        long? duration)
    {
        var id = Guid.NewGuid();
        _db.Tracks.Add(new Track
        {
            Id = id,
            CambrianTrackId = CambrianId(id),
            Title = title,
            CreatorId = "creator-1",
            Visibility = visibility,
            AudioUrl = objectKey,
        });
        _db.TrackMedia.Add(new TrackMedia
        {
            TrackId = id,
            ObjectKey = objectKey,
            State = state,
            StateChangedAtUtc = DateTime.UtcNow,
            ValidatedAtUtc = state == TrackMediaStates.Ready ? DateTime.UtcNow : null,
            SizeBytes = size,
            ContentType = "audio/mpeg",
            ChecksumSha256 = state == TrackMediaStates.Ready ? new string('b', 64) : null,
            DurationMilliseconds = duration,
            ValidationVersion = state == TrackMediaStates.Ready ? "media-v1" : null,
            ConcurrencyToken = Guid.NewGuid(),
        });
        return id;
    }

    private static string CambrianId(Guid id) => $"CAMB-TRK-{id.ToString("N")[..8].ToUpperInvariant()}";

    private static async IAsyncEnumerable<StorageObjectMetadata> Objects(params StorageObjectMetadata[] items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.Yield();
        }
    }

    /// <summary>Fails at enumeration time, like a real S3/R2 paginator outage.</summary>
    private static async IAsyncEnumerable<StorageObjectMetadata> FailingListing(Exception failure)
    {
        await Task.Yield();
        if (failure is not null)
            throw failure;
        yield break;
    }
}
