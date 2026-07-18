using Cambrian.Application.DTOs.Playback;

namespace Cambrian.Application.Interfaces;

public interface IPlaybackAnalyticsService
{
    Task CaptureAcceptedAsync(PlaybackAnalyticsEvent playEvent, CancellationToken ct = default);

    /// <summary>
    /// Best-effort mirror of a validated browser playback-telemetry batch
    /// (POST /api/v1/playback/telemetry). Callers forward only events that already
    /// passed endpoint validation — closed event/error enums, host-only media
    /// routing data, and no URLs, query strings, or ticket-shaped session ids —
    /// so signed playback URLs and tickets can never reach the sink.
    /// Implementations must fail open (log and return) rather than throw; the
    /// default is a no-op so lightweight implementations only opt in when they
    /// have somewhere to send the events.
    /// </summary>
    Task CaptureBrowserTelemetryAsync(IReadOnlyList<PlaybackTelemetryEvent> events, CancellationToken ct = default) =>
        Task.CompletedTask;
}

public sealed record PlaybackAnalyticsEvent(
    Guid EventId,
    Guid TrackId,
    string CreatorId,
    string DistinctId,
    DateTime QualifiedAtUtc,
    double ActivePlaybackSeconds,
    double ThresholdSeconds,
    bool IsAnonymous);
