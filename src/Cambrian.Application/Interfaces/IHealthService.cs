using Cambrian.Application.DTOs.Admin;

namespace Cambrian.Application.Interfaces;

/// <summary>
/// Service for health / diagnostics checks (DB connectivity, entity counts, storage).
/// </summary>
public interface IHealthService
{
    /// <summary>Runs a health check including DB connectivity and entity counts.</summary>
    Task<object> GetHealthAsync();

    /// <summary>Returns protected qualified-play aggregation and chart freshness details.</summary>
    Task<PlayPipelineHealthDetails> GetPlayPipelineHealthAsync(CancellationToken ct = default);

    /// <summary>Checks storage state for a sample of tracks.</summary>
    Task<object> GetStorageDiagAsync();

    /// <summary>
    /// Audits ALL tracks for missing audio files in storage.
    /// Returns a summary with counts and a list of track IDs whose audio is missing.
    /// </summary>
    Task<object> AuditAudioKeysAsync();
}
