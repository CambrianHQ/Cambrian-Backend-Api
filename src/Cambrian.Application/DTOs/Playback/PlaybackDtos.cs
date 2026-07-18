namespace Cambrian.Application.DTOs.Playback;

public sealed class PlaybackStartRequest
{
    public string? TrackId { get; set; }

    public string? ClientEventId { get; set; }

    // Retained for backward compatibility; the server always uses the stored title.
    public string? Title { get; set; }
}

public sealed class PlaybackStopRequest
{
    public string? StreamId { get; set; }

    public double? ActivePlaybackSeconds { get; set; }

    public double? PausedSeconds { get; set; }

    public int? SeekCount { get; set; }

    public double? EndingPositionSeconds { get; set; }

    public string? ClientEventId { get; set; }
}

public sealed record PlaybackStartResponse(
    string? StreamId,
    string Status,
    double QualificationThresholdSeconds,
    int DeduplicationWindowMinutes,
    DateTime ServerTimeUtc,
    bool AnonymousSessionAccepted);

public sealed record PlaybackStopResponse(
    string StreamId,
    string Status,
    bool Qualified,
    bool Counted,
    bool IdempotentReplay,
    double ActivePlaybackSeconds,
    double QualificationThresholdSeconds,
    DateTime? QualifiedAtUtc,
    long? LifetimePlayCount,
    DateTime ServerTimeUtc);

public sealed record PlaybackErrorResponse(
    bool Success,
    PlaybackError Error);

public sealed record PlaybackError(
    string Code,
    string Message,
    string CorrelationId);
