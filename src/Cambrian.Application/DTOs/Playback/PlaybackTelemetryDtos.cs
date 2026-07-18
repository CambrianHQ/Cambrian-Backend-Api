using System.Text.Json.Serialization;

namespace Cambrian.Application.DTOs.Playback;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PlaybackTelemetryBatch
{
    public IReadOnlyList<PlaybackTelemetryEvent> Events { get; init; } = Array.Empty<PlaybackTelemetryEvent>();
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PlaybackTelemetryEvent
{
    public string Event { get; init; } = "";
    public Guid TrackId { get; init; }
    public string PlaybackSessionId { get; init; } = "";
    public string FrontendRelease { get; init; } = "";
    public string BackendRelease { get; init; } = "";
    public string RequestId { get; init; } = "";
    public string BrowserFamily { get; init; } = "";
    public string BrowserVersion { get; init; } = "";
    public string MediaErrorCode { get; init; } = "none";
    public int ReadyState { get; init; }
    public int NetworkState { get; init; }
    public int? TimeToFirstPlaybackMs { get; init; }
    public int StallCount { get; init; }
    public string MediaHost { get; init; } = "";
    public string ResponseStatusCategory { get; init; } = "unknown";
    public DateTime EventTimestampUtc { get; init; }
}
