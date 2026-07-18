namespace Cambrian.Application.Interfaces;

public interface IPlaybackTrackingService
{
    Task<PlaybackStartResult> StartAsync(PlaybackStartCommand command, CancellationToken ct = default);
    Task<PlaybackStopResult> StopAsync(PlaybackStopCommand command, CancellationToken ct = default);
}

public sealed record PlaybackStartCommand(
    Guid TrackId,
    string? UserId,
    bool IsAdmin,
    string? AnonymousSessionId,
    string? UserAgent,
    string? IdempotencyKey);

public sealed record PlaybackStopCommand(
    Guid PlaybackSessionId,
    string? UserId,
    bool IsAdmin,
    string? AnonymousSessionId,
    string? UserAgent,
    string? IdempotencyKey,
    double? ActivePlaybackSeconds,
    double? PausedSeconds,
    int? SeekCount,
    double? EndingPositionSeconds);

public sealed record PlaybackStartResult(
    Guid? PlaybackSessionId,
    string Status,
    double QualificationThresholdSeconds,
    int DeduplicationWindowMinutes,
    DateTime ServerTimeUtc,
    bool AnonymousSessionAccepted);

public sealed record PlaybackStopResult(
    Guid PlaybackSessionId,
    string Status,
    bool Qualified,
    bool Counted,
    bool IdempotentReplay,
    double ActivePlaybackSeconds,
    double QualificationThresholdSeconds,
    DateTime? QualifiedAtUtc,
    long? LifetimePlayCount,
    DateTime ServerTimeUtc);

public sealed class PlaybackTrackingException : Exception
{
    public PlaybackTrackingException(string code, string message, int statusCode) : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }

    public string Code { get; }
    public int StatusCode { get; }
}
