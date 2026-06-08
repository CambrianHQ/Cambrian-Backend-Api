using Cambrian.Application.DTOs.ReleaseReady;

namespace Cambrian.Application.Interfaces;

/// <summary>
/// A pluggable mastering engine. Two implementations, selected by config:
/// <list type="bullet">
///   <item><c>TonnEngine</c> (RoEx API) — primary once the key arrives; produces a
///   preview the creator approves before the final master is retrieved.</item>
///   <item><c>FfmpegEngine</c> — fallback; two-pass loudnorm to −14 LUFS / −1 dBTP,
///   delivering the final master in one shot (no approval step).</item>
/// </list>
/// The pipeline must never block on RoEx availability — the ffmpeg engine is the
/// default and is fully functional on its own.
/// </summary>
public interface IMasteringEngine
{
    /// <summary>Engine identifier: <c>ffmpeg</c> | <c>tonn</c>.</summary>
    string Name { get; }

    /// <summary>
    /// True when the engine produces a preview the creator must approve before the
    /// final master is delivered (Tonn). When false the engine masters in one shot
    /// and <see cref="FinalizeAsync"/> is never called (ffmpeg).
    /// </summary>
    bool RequiresApproval { get; }

    /// <summary>
    /// Run mastering. For one-shot engines this returns the final WAV/MP3. For
    /// preview engines this returns a preview (<see cref="MasteringEngineResult.AwaitingApproval"/>
    /// = true) plus an <see cref="MasteringEngineResult.EngineRef"/> to finalize later.
    /// </summary>
    Task<MasteringEngineResult> MasterAsync(MasteringEngineRequest request, CancellationToken ct = default);

    /// <summary>
    /// Retrieve the final master after the creator approves the preview (preview
    /// engines only). One-shot engines throw <see cref="NotSupportedException"/>.
    /// </summary>
    Task<MasteringEngineResult> FinalizeAsync(MasteringEngineRequest request, string engineRef, CancellationToken ct = default);
}
