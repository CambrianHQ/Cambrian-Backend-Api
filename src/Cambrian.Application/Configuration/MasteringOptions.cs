namespace Cambrian.Application.Configuration;

/// <summary>
/// Release Ready mastering configuration. Bound from the <c>Mastering</c> config
/// section. The <see cref="Engine"/> switch selects the active
/// <c>IMasteringEngine</c> at startup; default is <c>ffmpeg</c> so the pipeline
/// works without RoEx.
/// </summary>
public sealed class MasteringOptions
{
    public const string SectionName = "Mastering";

    /// <summary>Active engine: <c>ffmpeg</c> (default) | <c>tonn</c>.</summary>
    public string Engine { get; set; } = "ffmpeg";

    /// <summary>Target integrated loudness in LUFS.</summary>
    public double TargetLufs { get; set; } = -14.0;

    /// <summary>Target true-peak ceiling in dBTP.</summary>
    public double TargetTruePeakDbtp { get; set; } = -1.0;

    /// <summary>Per-job timeout in seconds (kills runaway ffmpeg).</summary>
    public int JobTimeoutSeconds { get; set; } = 480;

    /// <summary>Minimum accepted track duration in seconds before mastering.</summary>
    public int MinDurationSeconds { get; set; } = 5;

    /// <summary>Maximum accepted track duration in seconds before mastering.</summary>
    public int MaxDurationSeconds { get; set; } = 15 * 60;

    /// <summary>How long a worker owns a processing job before another worker may recover it.</summary>
    public int ProcessingLeaseSeconds { get; set; } = 120;

    /// <summary>How often a worker extends its active processing lease.</summary>
    public int ProcessingHeartbeatSeconds { get; set; } = 30;

    /// <summary>Number of retry attempts after the first processing failure.</summary>
    public int MaxRetryCount { get; set; } = 1;

    public FfmpegOptions Ffmpeg { get; set; } = new();

    public TonnOptions Tonn { get; set; } = new();
}

public sealed class FfmpegOptions
{
    /// <summary>Path to the ffmpeg binary (default resolves on PATH; Docker installs it).</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";
}

public sealed class TonnOptions
{
    /// <summary>RoEx Tonn API base URL.</summary>
    public string BaseUrl { get; set; } = "https://tonn.roexaudio.com";

    /// <summary>RoEx API key. Empty until approval — the engine refuses to run without it.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Musical style hint sent to RoEx (RoEx requires a style for mastering).</summary>
    public string DefaultMusicalStyle { get; set; } = "POP";

    /// <summary>Seconds to poll for task completion before giving up.</summary>
    public int PollTimeoutSeconds { get; set; } = 180;
}
