namespace Cambrian.Application.Configuration;

/// <summary>Server-authoritative qualified-play rules.</summary>
public sealed class PlaybackOptions
{
    public const string SectionName = "Playback";

    /// <summary>Absolute qualification ceiling.</summary>
    public double QualificationSeconds { get; set; } = 30;

    /// <summary>Fraction of a known track duration used when lower than the ceiling.</summary>
    public double QualificationTrackFraction { get; set; } = 0.5;

    /// <summary>One listener may qualify once per track during this rolling UTC window.</summary>
    public int DeduplicationWindowMinutes { get; set; } = 60;

    /// <summary>Maximum client-reported active time accepted for a single segment.</summary>
    public double MaximumSegmentSeconds { get; set; } = 21_600;

    /// <summary>
    /// Optional dedicated HMAC secret for anonymous identifiers. If omitted, the API
    /// uses Jwt:Key so a stable configured secret still protects the hashes.
    /// </summary>
    public string? ListenerHashSecret { get; set; }
}
