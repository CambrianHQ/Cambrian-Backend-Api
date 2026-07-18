using System.ComponentModel.DataAnnotations;

namespace Cambrian.Application.Configuration;

public sealed class PlaybackMediaOptions
{
    public const string SectionName = "PlaybackMedia";

    public string? TicketSigningKey { get; set; }

    [Range(1, 60)]
    public int TicketLifetimeMinutes { get; set; } = 15;

    public bool ReadinessEnforcementEnabled { get; set; }
    public bool LegacyPublicStreamEnabled { get; set; } = true;

    [Range(1, 10_080)]
    public int ValidationMaxAgeMinutes { get; set; } = 1_440;

    [Range(1, 60)]
    public int ValidationTimeoutSeconds { get; set; } = 20;

    [Range(1, 86_400)]
    public int MinimumDurationSeconds { get; set; } = 3;

    [Range(1, 86_400)]
    public int MaximumDurationSeconds { get; set; } = 900;

    public string ValidationVersion { get; set; } = "media-v1";
    public string? ProductionPlaybackBaseUrl { get; set; }
    public string? ProductionProbeSigningKey { get; set; }
    public string BackendRelease { get; set; } = "unknown";
    public bool ReconciliationWorkerEnabled { get; set; }
    public bool ReconciliationWorkerRemediationEnabled { get; set; }

    [Range(5, 1_440)]
    public int ReconciliationIntervalMinutes { get; set; } = 60;

    [Range(1, 500)]
    public int TelemetryMaxEventsPerBatch { get; set; } = 50;

    [Range(1_024, 262_144)]
    public int TelemetryMaxPayloadBytes { get; set; } = 65_536;
}
