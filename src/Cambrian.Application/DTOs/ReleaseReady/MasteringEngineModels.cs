namespace Cambrian.Application.DTOs.ReleaseReady;

/// <summary>Input to an <c>IMasteringEngine</c>.</summary>
public sealed class MasteringEngineRequest
{
    /// <summary>Source audio bytes. Used by local engines (ffmpeg). May be null for
    /// URL-based engines (Tonn) that fetch from <see cref="SourceUrl"/>.</summary>
    public Stream? Source { get; init; }

    /// <summary>Signed URL to the source audio — used by URL-based engines (Tonn).</summary>
    public string? SourceUrl { get; init; }

    public string SourceFileName { get; init; } = "audio";

    public double TargetLufs { get; init; } = -14.0;

    public double TargetTruePeakDbtp { get; init; } = -1.0;
}

/// <summary>Output of an <c>IMasteringEngine</c>.</summary>
public sealed class MasteringEngineResult
{
    /// <summary>Mastered 44.1k/16-bit WAV bytes (null when not produced inline, e.g. preview-only).</summary>
    public byte[]? Wav { get; init; }

    /// <summary>Mastered 320 kbps MP3 bytes (null when not produced inline).</summary>
    public byte[]? Mp3 { get; init; }

    public double? InputLufs { get; init; }
    public double? OutputLufs { get; init; }
    public double? OutputTruePeakDbtp { get; init; }

    /// <summary>Engine-side reference (e.g. Tonn task id) needed to finalize after approval.</summary>
    public string? EngineRef { get; init; }

    /// <summary>Preview download URL (preview engines only).</summary>
    public string? PreviewUrl { get; init; }

    /// <summary>True when the engine produced a preview that must be approved before the
    /// final master is available (Tonn). False engines deliver the final inline (ffmpeg).</summary>
    public bool AwaitingApproval { get; init; }
}
