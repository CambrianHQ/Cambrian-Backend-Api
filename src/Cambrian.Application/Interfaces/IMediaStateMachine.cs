using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

public sealed record MediaStateMetadata(
    string? ObjectKey = null,
    string? FailureCode = null,
    string? FailureDetail = null,
    DateTime? ValidatedAtUtc = null,
    long? SizeBytes = null,
    string? ContentType = null,
    string? ChecksumSha256 = null,
    long? DurationMilliseconds = null,
    string? ValidationVersion = null);

public interface IMediaStateMachine
{
    Task<TrackMedia> InitializeLegacyAsync(
        Guid trackId,
        string? legacyAudioLocation,
        CancellationToken ct = default);

    Task<TrackMedia> TransitionAsync(
        Guid trackId,
        Guid expectedConcurrencyToken,
        string targetState,
        MediaStateMetadata metadata,
        CancellationToken ct = default);

    Task<TrackMedia> RefreshValidationAsync(
        Guid trackId,
        Guid expectedConcurrencyToken,
        MediaStateMetadata metadata,
        CancellationToken ct = default);
}
